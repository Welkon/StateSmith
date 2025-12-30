#nullable enable

using System.Collections.Generic;
using StateSmith.SmGraph;
using System.Linq;
using StateSmith.Common;
using System;
using StateSmith.Output.Gil;
using System.Text;
using StateSmith.Output.UserConfig;
using StateSmith.Runner;
using StateSmith.Output.Algos.Balanced1;
using StateSmith.Input.Expansions;

namespace StateSmith.Output.Algos.Table1;

/// <summary>
/// Table-based state machine algorithm optimized for ROM size.
/// Uses constant transition tables and linear search instead of function pointers.
/// Best for resource-constrained MCUs with 2-8KB ROM.
/// ROM savings: ~70% compared to Balanced1/Balanced2.
/// Performance: Moderate (linear table lookup).
/// </summary>
public class AlgoTable1 : IGilAlgo
{
    protected readonly RenderConfigBaseVars renderConfig;
    protected readonly NameMangler mangler;
    protected readonly OutputFile file;
    protected readonly EnumBuilder enumBuilder;
    protected readonly IAlgoEventIdToString algoEventIdToString;
    protected readonly IAlgoStateIdToString algoStateIdToString;
    protected readonly StandardFileHeaderPrinter standardFileHeaderPrinter;
    private readonly GilCreationHelper gilCreationHelper;
    protected readonly WrappingExpander wrappingExpander;

    protected StateMachine? _sm;
    protected const string AlgoWikiLink = "https://github.com/StateSmith/StateSmith/wiki/Algorithms";

    protected StateMachine Sm => _sm.ThrowIfNull("Must be set before use");

    public AlgoTable1(NameMangler mangler, EnumBuilder enumBuilder, RenderConfigBaseVars renderConfig, CodeStyleSettings styler, IAlgoEventIdToString algoEventIdToString, IAlgoStateIdToString algoStateIdToString, StandardFileHeaderPrinter standardFileHeaderPrinter, IExpander expander, UserExpansionScriptBases userExpansionScriptBases)
    {
        this.mangler = mangler;
        this.file = new OutputFile(styler, new StringBuilder());
        this.enumBuilder = enumBuilder;
        this.renderConfig = renderConfig;
        this.algoEventIdToString = algoEventIdToString;
        this.algoStateIdToString = algoStateIdToString;
        this.standardFileHeaderPrinter = standardFileHeaderPrinter;
        this.gilCreationHelper = new GilCreationHelper();
        this.wrappingExpander = new WrappingExpander(expander, userExpansionScriptBases);
    }

    public string GenerateGil(StateMachine sm)
    {
        this._sm = sm;
        mangler.SetStateMachine(sm);

        OutputFileTopComment();

        file.AppendIndentedLine($"// Generated state machine");
        file.AppendIndented($"public class {mangler.SmTypeName}");

        file.StartCodeBlock();
        GenerateInner();
        GilCreationHelper.AppendGilHelpersFuncs(file);
        file.FinishCodeBlock();

        return file.ToString();
    }

    private void OutputFileTopComment()
    {
        GilCreationHelper.AddFileTopComment(file, standardFileHeaderPrinter.GetFileGilHeader() +
            $"// Algorithm: {nameof(AlgorithmId.Table1)}. See {AlgoWikiLink}\n");
    }

    private void GenerateInner()
    {
        // Generate enums
        enumBuilder.OutputEventIdCode(file);
        file.AppendIndentedLine();
        enumBuilder.OutputStateIdCode(file);
        file.AppendIndentedLine();

        // Generate history enums (if any history states exist)
        foreach (var h in Sm.historyStates)
        {
            enumBuilder.OutputHistoryIdCode(file, h);
            file.AppendIndentedLine();
        }

        // Generate transition table structure
        OutputTransitionTableStruct();

        // Generate guard enum
        OutputGuardEnum();

        // Generate action enum
        OutputActionEnum();

        // Generate state machine struct
        OutputSmStruct();

        // Generate static arrays inside class (after enums and structs, before functions)
        OutputStateParentMapping();
        OutputStateDepthMapping();
        OutputTransitionTableData();

        // Generate function declarations
        OutputFuncCtor();
        OutputFuncStart();
        OutputFuncDispatchEvent();
        OutputPerformTransitionFunction();

        // Generate guard evaluator
        OutputGuardEvaluator();

        // Generate action executor
        OutputActionExecutor();

        // Generate hierarchical transition helpers
        OutputGetStateParentFunction();
        OutputGetStateDepthFunction();
        OutputExitUpToFunction();
        OutputEnterDownToFunction();

        // Generate state enter/exit dispatchers
        OutputStateEnterDispatcher();
        OutputStateExitDispatcher();

        // Generate state enter/exit functions (simplified)
        OutputStateHandlers();

        // Generate to_string functions if needed
        MaybeOutputToStringFunctions();
    }

    private void OutputTransitionTableStruct()
    {
        file.AppendIndentedLine("// Transition table entry structure");
        file.AppendIndented("private struct StateTransition");
        file.StartCodeBlock();
        file.AppendIndentedLine($"public {mangler.SmStateEnumType} current_state;");
        file.AppendIndentedLine($"public {mangler.SmEventEnumType} trigger;");
        file.AppendIndentedLine($"public {mangler.SmStateEnumType} next_state;");
        file.AppendIndentedLine("public ushort action_index;");
        file.AppendIndentedLine("public ushort guard_index;");
        file.FinishCodeBlock();
        file.AppendIndentedLine();
    }

    private readonly Dictionary<Behavior, int> behaviorToActionIndex = new();
    private readonly Dictionary<Behavior, int> behaviorToGuardIndex = new();
    private int actionCounter = 1;  // 0 is reserved for ACTION_NONE
    private int guardCounter = 1;   // 0 is reserved for GUARD_NONE

    private void OutputGuardEnum()
    {
        file.AppendIndentedLine("// Guard identifiers for transition table");
        file.AppendIndented("private enum GuardId");
        file.StartCodeBlock();

        file.AppendIndentedLine("GUARD_NONE = 0,");

        // Collect all unique guards with unique indices
        foreach (var state in Sm.GetNamedVerticesCopy())
        {
            foreach (var behavior in state.Behaviors)
            {
                if (behavior.HasTransition() && behavior.HasGuardCode())
                {
                    if (!behaviorToGuardIndex.ContainsKey(behavior))
                    {
                        behaviorToGuardIndex[behavior] = guardCounter;
                        var guardName = GetGuardName(state, behavior, guardCounter);
                        file.AppendIndentedLine($"{guardName} = {guardCounter},");
                        guardCounter++;
                    }
                }
            }
        }

        file.FinishCodeBlock();
        file.AppendIndentedLine();
    }

    private string GetGuardName(NamedVertex state, Behavior behavior, int index)
    {
        // State name is already mangled by ToUpper() in SmStateEnumValue
        return $"GUARD_{state.Name}_{index}".ToUpper();
    }

    private void OutputActionEnum()
    {
        file.AppendIndentedLine("// Action identifiers for transition table");
        file.AppendIndented("private enum ActionId");
        file.StartCodeBlock();

        file.AppendIndentedLine("ACTION_NONE = 0,");

        // Collect all unique actions with unique indices
        foreach (var state in Sm.GetNamedVerticesCopy())
        {
            foreach (var behavior in state.Behaviors)
            {
                if (behavior.HasTransition())
                {
                    if (!behaviorToActionIndex.ContainsKey(behavior))
                    {
                        behaviorToActionIndex[behavior] = actionCounter;
                        var actionName = GetActionName(state, behavior, actionCounter);
                        file.AppendIndentedLine($"{actionName} = {actionCounter},");
                        actionCounter++;
                    }
                }
            }
        }

        file.FinishCodeBlock();
        file.AppendIndentedLine();
    }

    private string GetActionName(NamedVertex state, Behavior behavior, int index)
    {
        // Use sanitized names and ToUpper for C# identifier safety
        var trigger = behavior.Triggers.FirstOrDefault() ?? "NO_TRIGGER";
        var triggerName = TriggerHelper.SanitizeTriggerName(trigger);
        return $"ACTION_{state.Name}_{triggerName}_{index}".ToUpper();
    }

    private void OutputSmStruct()
    {
        file.AppendIndentedLine($"// Used internally by state machine. Feel free to inspect, but don't modify.");
        file.AppendIndentedLine($"public {mangler.SmStateEnumType} {mangler.SmStateIdVarName};");

        if (IsVarsStructNeeded())
        {
            file.AppendIndentedLine();
            file.AppendIndentedLine("// State machine variables. Can be used for inputs, outputs, user variables...");
            file.AppendIndented("public struct Vars");
            file.StartCodeBlock();

            foreach (var line in StringUtils.SplitIntoLinesOrEmpty(Sm.variables.Trim()))
            {
                file.AppendIndentedLine("public " + line);
            }

            foreach (var line in StringUtils.SplitIntoLinesOrEmpty(renderConfig.VariableDeclarations.Trim()))
            {
                file.AppendIndentedLine(gilCreationHelper.WrapRawCodeAsField(line));
            }

            file.FinishCodeBlock();

            file.AppendIndentedLine();
            file.AppendIndentedLine("// Variables. Can be used for inputs, outputs, user variables...");
            file.AppendIndentedLine("public Vars vars = new Vars();");
        }
    }

    private bool IsVarsStructNeeded()
    {
        if (Sm.variables.Length > 0)
        {
            return true;
        }

        return StringUtils.RemoveCCodeComments(renderConfig.VariableDeclarations).Trim().Length > 0;
    }

    private void OutputStateParentMapping()
    {
        var states = Sm.GetNamedVerticesCopy();

        file.AppendIndentedLine("// State parent mapping for hierarchical transitions (ROM stored)");
        file.AppendIndented($"private static readonly {mangler.SmStateEnumType}[] stateParents = new {mangler.SmStateEnumType}[]");
        file.StartCodeBlock();

        foreach (var state in states)
        {
            // Get parent state ID (ROOT for top-level states)
            var parentState = state.Parent as NamedVertex;
            var parentEnum = parentState != null
                ? $"{mangler.SmStateEnumType}.{mangler.SmStateEnumValue(parentState)}"
                : $"{mangler.SmStateEnumType}.ROOT";

            file.AppendIndentedLine($"{parentEnum},  // Parent of {mangler.SmStateEnumValue(state)}");
        }

        file.FinishCodeBlock(codeAfterBrace: ";", forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void OutputStateDepthMapping()
    {
        var states = Sm.GetNamedVerticesCopy();

        file.AppendIndentedLine("// State depth mapping for hierarchical transitions (ROM stored)");
        file.AppendIndented($"private static readonly int[] stateDepths = new int[]");
        file.StartCodeBlock();

        foreach (var state in states)
        {
            file.AppendIndentedLine($"{state.Depth},  // Depth of {mangler.SmStateEnumValue(state)}");
        }

        file.FinishCodeBlock(codeAfterBrace: ";", forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void OutputFuncCtor()
    {
        file.AppendIndentedLine();
        file.AppendIndentedLine("// State machine constructor. Must be called before start or dispatch event functions. Not thread safe.");
        file.AppendIndented($"public {mangler.SmTypeName}()");
        file.StartCodeBlock();
        file.FinishCodeBlock();
        file.AppendIndentedLine();
    }

    private void OutputFuncStart()
    {
        file.AppendIndentedLine("// Starts the state machine. Must be called before dispatching events. Not thread safe.");
        file.AppendIndented($"public void {mangler.SmStartFuncName}()");
        file.StartCodeBlock();

        // Find initial state and its transition
        var initialState = Sm.Children.OfType<InitialState>().FirstOrDefault();
        if (initialState != null && initialState.Children.Count > 0)
        {
            var targetVertex = initialState.Children[0];
            if (targetVertex is NamedVertex namedTarget)
            {
                file.AppendIndentedLine($"// Enter ROOT state");
                file.AppendIndentedLine($"ROOT_enter();");
                file.AppendIndentedLine();

                // Execute initial transition action (if any)
                var initialBehavior = initialState.Behaviors.FirstOrDefault(b => b.HasTransition());
                if (initialBehavior != null && initialBehavior.HasActionCode())
                {
                    file.AppendIndentedLine("// Execute initial transition action");
                    file.AppendIndentedLine(initialBehavior.actionCode);
                    file.AppendIndentedLine();
                }

                // Set initial state
                file.AppendIndentedLine($"this.{mangler.SmStateIdVarName} = {mangler.SmStateEnumType}.{mangler.SmStateEnumValue(namedTarget)};");
                file.AppendIndentedLine();

                // Enter all ancestor states from ROOT down to target
                file.AppendIndentedLine($"// Enter all ancestor states from ROOT down to target state");
                file.AppendIndentedLine($"EnterDownTo({mangler.SmStateEnumType}.ROOT, {mangler.SmStateEnumType}.{mangler.SmStateEnumValue(namedTarget)});");
                file.AppendIndentedLine();

                // Dispatch DO event for completion transitions (if state machine uses DO event)
                if (Sm._events.Contains(TriggerHelper.TRIGGER_DO))
                {
                    file.AppendIndentedLine("// Auto-dispatch DO event for completion transitions");
                    file.AppendIndentedLine($"{mangler.SmDispatchEventFuncName}({mangler.SmEventEnumType}.{mangler.SmEventEnumValue(TriggerHelper.TRIGGER_DO)});");
                }
            }
        }

        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void OutputFuncDispatchEvent()
    {
        string eventIdParam = mangler.MangleVarName("event_id");

        file.AppendIndentedLine("// Dispatches an event to the state machine. Not thread safe.");
        file.AppendIndented($"public void {mangler.SmDispatchEventFuncName}({mangler.SmEventEnumType} {eventIdParam})");
        file.StartCodeBlock();

        file.AppendIndentedLine("// Linear search through transition table");
        file.AppendIndentedLine($"for (int i = 0; i < {transitionCount}; i++)");
        file.StartCodeBlock();

        file.AppendIndentedLine($"if (transitions[i].current_state == this.{mangler.SmStateIdVarName} &&");
        file.AppendIndentedLine($"    transitions[i].trigger == {eventIdParam})");
        file.StartCodeBlock();

        file.AppendIndentedLine("// Evaluate guard if present");
        file.AppendIndentedLine($"if (transitions[i].guard_index != 0 && !EvaluateGuard(transitions[i].guard_index))");
        file.StartCodeBlock();
        file.AppendIndentedLine("// Guard failed, try next transition");
        file.AppendIndentedLine("continue;");
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();

        file.AppendIndentedLine("// Perform hierarchical state transition");
        file.AppendIndentedLine("// For flat state machines, this is equivalent to simple exit->action->enter");
        file.AppendIndentedLine("// For hierarchical state machines, this properly handles parent/child states");
        file.AppendIndentedLine($"PerformTransition(this.{mangler.SmStateIdVarName}, transitions[i].next_state, transitions[i].action_index);");
        file.AppendIndentedLine("return;");

        file.FinishCodeBlock(forceNewLine: true);
        file.FinishCodeBlock(forceNewLine: true);

        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void OutputPerformTransitionFunction()
    {
        file.AppendIndentedLine("// Performs a hierarchical state transition");
        file.AppendIndentedLine("// Exits states up to LCA, executes action, enters states down to target");
        file.AppendIndented($"private void PerformTransition({mangler.SmStateEnumType} from_state, {mangler.SmStateEnumType} to_state, ushort action_index)");
        file.StartCodeBlock();

        file.AppendIndentedLine("// Handle self-transition");
        file.AppendIndentedLine("if (from_state == to_state)");
        file.StartCodeBlock();
        file.AppendIndentedLine("CallStateExit(from_state);");
        file.AppendIndentedLine("ExecuteAction(action_index);");
        file.AppendIndentedLine("CallStateEnter(to_state);");
        file.AppendIndentedLine($"this.{mangler.SmStateIdVarName} = to_state;");

        // Auto-dispatch DO event after self-transition
        if (Sm._events.Contains(TriggerHelper.TRIGGER_DO))
        {
            file.AppendIndentedLine();
            file.AppendIndentedLine("// Auto-dispatch DO event for completion transitions");
            file.AppendIndentedLine($"{mangler.SmDispatchEventFuncName}({mangler.SmEventEnumType}.{mangler.SmEventEnumValue(TriggerHelper.TRIGGER_DO)});");
        }

        file.AppendIndentedLine("return;");
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();

        file.AppendIndentedLine("// Find Least Common Ancestor (LCA) for hierarchical transition");
        file.AppendIndentedLine($"int fromDepth = GetStateDepth(from_state);");
        file.AppendIndentedLine($"int toDepth = GetStateDepth(to_state);");
        file.AppendIndentedLine($"{mangler.SmStateEnumType} fromCurrent = from_state;");
        file.AppendIndentedLine($"{mangler.SmStateEnumType} toCurrent = to_state;");
        file.AppendIndentedLine();

        file.AppendIndentedLine("// Bring both states to same depth");
        file.AppendIndentedLine("while (fromDepth > toDepth)");
        file.StartCodeBlock();
        file.AppendIndentedLine("fromCurrent = GetStateParent(fromCurrent);");
        file.AppendIndentedLine("fromDepth--;");
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();

        file.AppendIndentedLine("while (toDepth > fromDepth)");
        file.StartCodeBlock();
        file.AppendIndentedLine("toCurrent = GetStateParent(toCurrent);");
        file.AppendIndentedLine("toDepth--;");
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();

        file.AppendIndentedLine("// Find LCA by ascending both paths");
        file.AppendIndentedLine("while (fromCurrent != toCurrent)");
        file.StartCodeBlock();
        file.AppendIndentedLine("fromCurrent = GetStateParent(fromCurrent);");
        file.AppendIndentedLine("toCurrent = GetStateParent(toCurrent);");
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();

        file.AppendIndentedLine($"{mangler.SmStateEnumType} lca = fromCurrent;");
        file.AppendIndentedLine();

        file.AppendIndentedLine("// Step 1: Exit from current state up to (but not including) LCA");
        file.AppendIndentedLine("ExitUpTo(from_state, lca);");
        file.AppendIndentedLine();

        file.AppendIndentedLine("// Step 2: Execute transition action");
        file.AppendIndentedLine("ExecuteAction(action_index);");
        file.AppendIndentedLine();

        file.AppendIndentedLine("// Step 3: Enter from LCA down to target state");
        file.AppendIndentedLine("EnterDownTo(lca, to_state);");
        file.AppendIndentedLine($"this.{mangler.SmStateIdVarName} = to_state;");

        // Auto-dispatch DO event after regular transition
        if (Sm._events.Contains(TriggerHelper.TRIGGER_DO))
        {
            file.AppendIndentedLine();
            file.AppendIndentedLine("// Auto-dispatch DO event for completion transitions");
            file.AppendIndentedLine($"{mangler.SmDispatchEventFuncName}({mangler.SmEventEnumType}.{mangler.SmEventEnumValue(TriggerHelper.TRIGGER_DO)});");
        }

        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private class TransitionEntry
    {
        public NamedVertex CurrentState { get; set; } = null!;
        public string Trigger { get; set; } = "";
        public NamedVertex NextState { get; set; } = null!;
        public Behavior Behavior { get; set; } = null!;
        public bool IsInherited { get; set; }
    }

    private List<TransitionEntry> CollectAllTransitions()
    {
        var transitions = new List<TransitionEntry>();
        var states = Sm.GetNamedVerticesCopy();

        foreach (var state in states)
        {
            // Skip StateMachine itself (root)
            if (state is StateMachine)
                continue;

            // Collect direct transitions from this state
            var handledTriggers = new HashSet<string>();

            foreach (var behavior in state.Behaviors)
            {
                if (behavior.HasTransition() && behavior.TransitionTarget is NamedVertex target)
                {
                    // Process ALL triggers for this behavior (not just the first one)
                    foreach (var trigger in behavior.Triggers)
                    {
                        if (TriggerHelper.IsEvent(trigger))
                        {
                            var triggerName = TriggerHelper.SanitizeTriggerName(trigger);
                            handledTriggers.Add(triggerName);

                            transitions.Add(new TransitionEntry
                            {
                                CurrentState = state,
                                Trigger = triggerName,  // Store sanitized trigger name
                                NextState = target,
                                Behavior = behavior,
                                IsInherited = false
                            });
                        }
                    }
                }
            }

            // Collect inherited transitions from ancestor states
            Vertex? ancestor = state.Parent;
            while (ancestor != null)
            {
                if (ancestor is NamedVertex ancestorState && !(ancestor is StateMachine))
                {
                    foreach (var ancestorBehavior in ancestorState.Behaviors)
                    {
                        if (ancestorBehavior.HasTransition() && ancestorBehavior.TransitionTarget is NamedVertex ancestorTarget)
                        {
                            // Process ALL triggers for this behavior
                            foreach (var trigger in ancestorBehavior.Triggers)
                            {
                                if (TriggerHelper.IsEvent(trigger))
                                {
                                    var triggerName = TriggerHelper.SanitizeTriggerName(trigger);

                                    // Only inherit if child state doesn't already handle this trigger
                                    if (!handledTriggers.Contains(triggerName))
                                    {
                                        handledTriggers.Add(triggerName);

                                        transitions.Add(new TransitionEntry
                                        {
                                            CurrentState = state,  // Use child state as current
                                            Trigger = triggerName,  // Store sanitized trigger name
                                            NextState = ancestorTarget,
                                            Behavior = ancestorBehavior,
                                            IsInherited = true
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
                ancestor = ancestor.Parent;
            }
        }

        return transitions;
    }

    private void OutputTransitionTableData()
    {
        var allTransitions = CollectAllTransitions();
        var transitionCount = allTransitions.Count;

        // Store transition count for later use
        this.transitionCount = transitionCount;

        file.AppendIndentedLine("// Transition table (stored in ROM as const data)");
        file.AppendIndentedLine("// Includes inherited transitions from parent states (pre-expanded for ROM optimization)");
        file.AppendIndented($"private static readonly StateTransition[] transitions = new StateTransition[]");
        file.StartCodeBlock();

        int index = 0;
        foreach (var entry in allTransitions)
        {
            var actionIndex = behaviorToActionIndex.ContainsKey(entry.Behavior) ? behaviorToActionIndex[entry.Behavior] : 0;
            var guardIndex = behaviorToGuardIndex.ContainsKey(entry.Behavior) ? behaviorToGuardIndex[entry.Behavior] : 0;

            var comment = entry.IsInherited ? " // Inherited from parent" : "";
            var comma = (index < allTransitions.Count - 1) ? "," : "";

            file.AppendIndented("new StateTransition {");
            file.AppendWithoutIndent($" current_state = {mangler.SmStateEnumType}.{mangler.SmStateEnumValue(entry.CurrentState)}, ");
            file.AppendWithoutIndent($"trigger = {mangler.SmEventEnumType}.{mangler.SmEventEnumValue(entry.Trigger)}, ");
            file.AppendWithoutIndent($"next_state = {mangler.SmStateEnumType}.{mangler.SmStateEnumValue(entry.NextState)}, ");
            file.AppendWithoutIndent($"action_index = {actionIndex}, ");
            file.AppendWithoutIndent($"guard_index = {guardIndex} }}{comma}{comment}\n");

            index++;
        }

        file.FinishCodeBlock(codeAfterBrace: ";", forceNewLine: true);
        file.AppendIndentedLine();
    }

    private int transitionCount = 0; // Store for dispatch_event function

    private void OutputGuardEvaluator()
    {
        file.AppendIndentedLine("// Evaluate guard based on guard index");
        file.AppendIndented("private bool EvaluateGuard(ushort guard_index)");
        file.StartCodeBlock();

        file.AppendIndented("switch ((GuardId)guard_index)");
        file.StartCodeBlock();

        file.AppendIndentedLine("case GuardId.GUARD_NONE:");
        file.IncreaseIndentLevel();
        file.AppendIndentedLine("return true;");
        file.DecreaseIndentLevel();

        // Generate cases for each guard
        foreach (var state in Sm.GetNamedVerticesCopy())
        {
            foreach (var behavior in state.Behaviors)
            {
                if (behavior.HasTransition() && behavior.HasGuardCode() && behaviorToGuardIndex.ContainsKey(behavior))
                {
                    var guardIndex = behaviorToGuardIndex[behavior];
                    var guardName = GetGuardName(state, behavior, guardIndex);
                    file.AppendIndentedLine($"case GuardId.{guardName}:");
                    file.IncreaseIndentLevel();
                    // Use wrappingExpander to expand user functions
                    var expandedGuardCode = wrappingExpander.ExpandWrapGuardCode(behavior);
                    file.AppendIndentedLine($"return {expandedGuardCode};");
                    file.DecreaseIndentLevel();
                }
            }
        }

        file.AppendIndentedLine("default:");
        file.IncreaseIndentLevel();
        file.AppendIndentedLine("return true;");
        file.DecreaseIndentLevel();

        file.FinishCodeBlock(forceNewLine: true);
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void OutputActionExecutor()
    {
        file.AppendIndentedLine("// Execute action based on action index");
        file.AppendIndented("private void ExecuteAction(ushort action_index)");
        file.StartCodeBlock();

        file.AppendIndented("switch ((ActionId)action_index)");
        file.StartCodeBlock();

        file.AppendIndentedLine("case ActionId.ACTION_NONE:");
        file.IncreaseIndentLevel();
        file.AppendIndentedLine("break;");
        file.DecreaseIndentLevel();

        // Generate cases for each action
        foreach (var state in Sm.GetNamedVerticesCopy())
        {
            foreach (var behavior in state.Behaviors)
            {
                if (behavior.HasTransition() && behaviorToActionIndex.ContainsKey(behavior))
                {
                    var actionIndex = behaviorToActionIndex[behavior];
                    var actionName = GetActionName(state, behavior, actionIndex);
                    file.AppendIndentedLine($"case ActionId.{actionName}:");
                    file.IncreaseIndentLevel();

                    // Output actual action code
                    if (behavior.HasActionCode())
                    {
                        // Use wrappingExpander to expand user functions
                        var expandedActionCode = wrappingExpander.ExpandWrapActionCode(behavior);
                        file.AppendIndentedLine(expandedActionCode);
                    }

                    file.AppendIndentedLine("break;");
                    file.DecreaseIndentLevel();
                }
            }
        }

        file.FinishCodeBlock(forceNewLine: true);
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void OutputStateEnterDispatcher()
    {
        file.AppendIndentedLine("// Call state enter function based on state ID");
        file.AppendIndented($"private void CallStateEnter({mangler.SmStateEnumType} state)");
        file.StartCodeBlock();

        file.AppendIndented("switch (state)");
        file.StartCodeBlock();

        foreach (var state in Sm.GetNamedVerticesCopy())
        {
            if (state is StateMachine)
                continue;

            file.AppendIndentedLine($"case {mangler.SmQualifiedStateEnumValue(state)}:");
            file.IncreaseIndentLevel();
            file.AppendIndentedLine($"{state.Name}_enter();");
            file.AppendIndentedLine("break;");
            file.DecreaseIndentLevel();
        }

        file.FinishCodeBlock(forceNewLine: true);
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void OutputStateExitDispatcher()
    {
        file.AppendIndentedLine("// Call state exit function based on state ID");
        file.AppendIndented($"private void CallStateExit({mangler.SmStateEnumType} state)");
        file.StartCodeBlock();

        file.AppendIndented("switch (state)");
        file.StartCodeBlock();

        foreach (var state in Sm.GetNamedVerticesCopy())
        {
            if (state is StateMachine)
                continue;

            file.AppendIndentedLine($"case {mangler.SmQualifiedStateEnumValue(state)}:");
            file.IncreaseIndentLevel();
            file.AppendIndentedLine($"{state.Name}_exit();");
            file.AppendIndentedLine("break;");
            file.DecreaseIndentLevel();
        }

        file.FinishCodeBlock(forceNewLine: true);
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void OutputStateHandlers()
    {
        file.AppendIndentedLine("// State enter/exit functions");

        // Generate ROOT_enter
        file.AppendIndented("private void ROOT_enter()");
        file.StartCodeBlock();

        // Find ROOT state behaviors
        var rootBehaviors = Sm.Behaviors.Where(b =>
            b.Triggers.Any(t => TriggerHelper.SanitizeTriggerName(t) == TriggerHelper.TRIGGER_ENTER));

        foreach (var behavior in rootBehaviors)
        {
            if (behavior.HasActionCode())
            {
                // Use ExpandWrapActionCode to wrap C code for GIL transpilation
                var expandedActionCode = wrappingExpander.ExpandWrapActionCode(behavior);
                file.AppendIndentedLine(expandedActionCode);
            }
        }

        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();

        // Generate enter/exit for each state
        foreach (var state in Sm.GetNamedVerticesCopy())
        {
            if (state is StateMachine)
                continue;

            // Enter function
            file.AppendIndented($"private void {state.Name}_enter()");
            file.StartCodeBlock();

            var enterBehaviors = state.Behaviors.Where(b =>
                b.Triggers.Any(t => TriggerHelper.SanitizeTriggerName(t) == TriggerHelper.TRIGGER_ENTER));

            foreach (var behavior in enterBehaviors)
            {
                if (behavior.HasActionCode())
                {
                    // Use ExpandWrapActionCode to wrap C code for GIL transpilation
                    var expandedActionCode = wrappingExpander.ExpandWrapActionCode(behavior);
                    file.AppendIndentedLine(expandedActionCode);
                }
            }

            file.FinishCodeBlock(forceNewLine: true);
            file.AppendIndentedLine();

            // Exit function
            file.AppendIndented($"private void {state.Name}_exit()");
            file.StartCodeBlock();

            var exitBehaviors = state.Behaviors.Where(b =>
                b.Triggers.Any(t => TriggerHelper.SanitizeTriggerName(t) == TriggerHelper.TRIGGER_EXIT));

            foreach (var behavior in exitBehaviors)
            {
                if (behavior.HasActionCode())
                {
                    // Use ExpandWrapActionCode to wrap C code for GIL transpilation
                    var expandedActionCode = wrappingExpander.ExpandWrapActionCode(behavior);
                    file.AppendIndentedLine(expandedActionCode);
                }
            }

            file.FinishCodeBlock(forceNewLine: true);
            file.AppendIndentedLine();
        }
    }

    private void OutputGetStateParentFunction()
    {
        var stateCount = Sm.GetNamedVerticesCopy().Count;

        file.AppendIndentedLine("// Get parent state ID for hierarchical transitions");
        file.AppendIndented($"private {mangler.SmStateEnumType} GetStateParent({mangler.SmStateEnumType} state)");
        file.StartCodeBlock();

        file.AppendIndentedLine($"int stateIndex = (int)state;");
        file.AppendIndentedLine($"if (stateIndex < 0 || stateIndex >= {stateCount})");
        file.IncreaseIndentLevel();
        file.AppendIndentedLine($"return {mangler.SmStateEnumType}.ROOT;");
        file.DecreaseIndentLevel();
        file.AppendIndentedLine($"return stateParents[stateIndex];");

        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void OutputGetStateDepthFunction()
    {
        var stateCount = Sm.GetNamedVerticesCopy().Count;

        file.AppendIndentedLine("// Get state depth for hierarchical transitions");
        file.AppendIndented($"private int GetStateDepth({mangler.SmStateEnumType} state)");
        file.StartCodeBlock();

        file.AppendIndentedLine($"int stateIndex = (int)state;");
        file.AppendIndentedLine($"if (stateIndex < 0 || stateIndex >= {stateCount})");
        file.IncreaseIndentLevel();
        file.AppendIndentedLine($"return 0;");
        file.DecreaseIndentLevel();
        file.AppendIndentedLine($"return stateDepths[stateIndex];");

        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void OutputExitUpToFunction()
    {
        file.AppendIndentedLine("// Exit states up to (but not including) the ancestor state");
        file.AppendIndented($"private void ExitUpTo({mangler.SmStateEnumType} currentState, {mangler.SmStateEnumType} ancestorState)");
        file.StartCodeBlock();

        file.AppendIndentedLine($"{mangler.SmStateEnumType} state = currentState;");
        file.AppendIndentedLine($"while (state != ancestorState && state != {mangler.SmStateEnumType}.ROOT)");
        file.StartCodeBlock();
        file.AppendIndentedLine($"CallStateExit(state);");
        file.AppendIndentedLine($"state = GetStateParent(state);");
        file.FinishCodeBlock(forceNewLine: true);

        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void OutputEnterDownToFunction()
    {
        // Calculate maximum state depth for fixed-size array
        var maxDepth = Sm.GetNamedVerticesCopy().Max(s => s.Depth);

        file.AppendIndentedLine("// Enter states from ancestor down to (and including) target state");
        file.AppendIndented($"private void EnterDownTo({mangler.SmStateEnumType} ancestorState, {mangler.SmStateEnumType} targetState)");
        file.StartCodeBlock();

        file.AppendIndentedLine("// Build path from ancestor to target using fixed-size array");
        file.AppendIndentedLine($"{mangler.SmStateEnumType}[] pathToTarget = new {mangler.SmStateEnumType}[{maxDepth + 1}];");
        file.AppendIndentedLine("int pathLength = 0;");
        file.AppendIndentedLine($"{mangler.SmStateEnumType} state = targetState;");
        file.AppendIndentedLine($"while (state != ancestorState && state != {mangler.SmStateEnumType}.ROOT)");
        file.StartCodeBlock();
        file.AppendIndentedLine($"pathToTarget[pathLength] = state;");
        file.AppendIndentedLine($"pathLength++;");
        file.AppendIndentedLine($"state = GetStateParent(state);");
        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();

        file.AppendIndentedLine("// Enter states from top down");
        file.AppendIndentedLine($"for (int i = pathLength - 1; i >= 0; i--)");
        file.StartCodeBlock();
        file.AppendIndentedLine($"CallStateEnter(pathToTarget[i]);");
        file.FinishCodeBlock(forceNewLine: true);

        file.FinishCodeBlock(forceNewLine: true);
        file.AppendIndentedLine();
    }

    private void MaybeOutputToStringFunctions()
    {
        // Output state_id_to_string function
        algoStateIdToString.CreateStateIdToStringFunction(file, Sm);

        // Output event_id_to_string function
        algoEventIdToString.CreateEventIdToStringFunction(file, Sm);
    }
}
