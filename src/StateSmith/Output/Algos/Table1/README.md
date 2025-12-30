# AlgoTable1 - Table-Based State Machine Algorithm

## Overview / 概述

AlgoTable1 is a ROM-optimized state machine code generation algorithm designed for resource-constrained embedded systems. It achieves **~70% ROM savings** compared to Balanced1/Balanced2 by using constant transition tables and linear search instead of function pointers.

AlgoTable1 是一个为资源受限的嵌入式系统设计的 ROM 优化状态机代码生成算法。通过使用常量转换表和线性搜索替代函数指针，相比 Balanced1/Balanced2 实现了 **约70%的ROM节省**。

## When to Use AlgoTable1 / 适用场景

### ✅ Ideal Use Cases / 理想使用场景

AlgoTable1 is **best suited for**:

- **Resource-constrained 8-bit MCUs** with limited ROM (2-8KB typical)
  - **资源受限的8位MCU**，ROM通常为 2-8KB
- **Small to moderate state machines** (< 20 states recommended)
  - **小到中等规模的状态机**（推荐 < 20个状态）
- Applications where **ROM size is the critical constraint**
  - **ROM大小是关键约束** 的应用场景
- **Moderate performance requirements** (ms-level response time acceptable)
  - **中等性能要求**（ms级响应时间可接受）
- Low-end consumer electronics, IoT sensors, simple control systems
  - 低端消费电子、物联网传感器、简单控制系统

### ❌ Not Recommended For / 不推荐场景

AlgoTable1 is **NOT suitable for**:

- Large state machines (> 30 states) - linear search becomes inefficient
  - 大型状态机（> 30个状态）- 线性搜索效率低下
- Real-time systems requiring **µs-level response time**
  - 需要 **微秒级响应时间** 的实时系统
- Systems with abundant ROM (use Balanced2 for better performance)
  - ROM充足的系统（使用 Balanced2 获得更好性能）
- Projects requiring orthogonal regions (not supported)
  - 需要正交区域的项目（不支持）

## Feature Support / 功能支持

| Feature / 功能                  | Status / 状态 | Notes / 说明 |
|--------------------------------|-------------|-------------|
| Basic event-driven transitions / 基本事件驱动转换 | ✅ Full | Core functionality / 核心功能 |
| Guard conditions / 守卫条件 | ✅ Full | Conditional transitions / 条件转换 |
| Transition actions / 转换动作 | ✅ Full | Execute code during transitions / 转换时执行代码 |
| Enter/Exit handlers / 进入/退出处理器 | ✅ Full | State lifecycle hooks / 状态生命周期钩子 |
| Hierarchical states / 层级状态 | ✅ Full | Child states inherit parent transitions / 子状态继承父状态转换 |
| Do/Completion transitions / Do/完成转换 | ✅ Full | Auto-dispatch DO event / 自动分发DO事件 |
| History states (shallow/deep) / 历史状态 | ✅ Optional | +2 bytes RAM per history state / 每个历史状态增加2字节RAM |
| Orthogonal regions / 正交区域 | ❌ Not supported | Causes ROM explosion / 导致ROM爆炸 |
| **Transpiler support / 转译器支持** | ⚠️ **C99 only** | Other languages planned / 其他语言计划中 |

## RAM/ROM Tradeoffs / RAM/ROM权衡

### ROM Savings / ROM节省

Compared to Balanced1/Balanced2:
- **Transition storage**: Function pointers (4-8 bytes) → Table entries (~6-8 bytes total)
- **No function prologue/epilogue overhead** for each state handler
- **Smaller code footprint**: Single dispatch loop vs multiple handler functions

与 Balanced1/Balanced2 相比：
- **转换存储**：函数指针（4-8字节）→ 表项（总计约6-8字节）
- **无函数序言/尾声开销**（每个状态处理器）
- **更小的代码占用**：单一分发循环 vs 多个处理器函数

**Typical savings**: 70% ROM reduction for simple state machines
**典型节省**：简单状态机减少70% ROM

### RAM Costs / RAM成本

| Feature / 功能 | RAM Cost / RAM成本 | Notes / 说明 |
|---------------|-------------------|-------------|
| Base state machine / 基本状态机 | ~2-4 bytes | Current state ID / 当前状态ID |
| Each variable / 每个变量 | Variable size / 变量大小 | User-defined / 用户定义 |
| **Each history state** / **每个历史状态** | **+2 bytes** | Stores last child state / 存储最后的子状态 |

**Example / 示例**:
```
State machine with 10 states + 2 history states:
- Base: 2 bytes (state ID)
- History: 4 bytes (2 × 2 bytes)
- Total: 6 bytes RAM

包含10个状态 + 2个历史状态的状态机：
- 基础：2字节（状态ID）
- 历史：4字节（2 × 2字节）
- 总计：6字节 RAM
```

## Performance Characteristics / 性能特征

### Transition Lookup / 转换查找

- **Algorithm**: Linear search through transition table / 线性搜索转换表
- **Complexity**: O(N) where N = number of transitions / 复杂度O(N)，N为转换数量
- **Typical case**: 5-15 transitions per state → 2-7 comparisons average
  - **典型情况**：每个状态5-15个转换 → 平均2-7次比较

### Response Time Estimates / 响应时间估算

For a typical 8-bit MCU @ 8-16 MHz:
- **Best case**: < 50 µs (first table entry matches)
- **Average case**: 100-300 µs (mid-table hit)
- **Worst case**: 500-1000 µs (last entry or no match)

典型8位MCU @ 8-16 MHz：
- **最佳情况**：< 50微秒（第一个表项匹配）
- **平均情况**：100-300微秒（中间表项命中）
- **最坏情况**：500-1000微秒（最后一项或无匹配）

## Usage Guide / 使用指南

### Basic Setup / 基本设置

```csharp
var settings = new RunnerSettings
{
    algorithmId = AlgorithmId.Table1,
    transpilerId = TranspilerId.C99  // Currently required / 当前必需
};
```

### Enabling History States / 启用历史状态

History states are **automatically supported** when you use them in your state machine diagram. No special configuration needed.

当你在状态机图中使用历史状态时，它们会 **自动支持**。无需特殊配置。

**Trade-off consideration / 权衡考虑**:
- Each history state costs **+2 bytes RAM**
- If RAM is extremely limited (< 100 bytes), avoid history states
- 每个历史状态需要 **+2字节RAM**
- 如果RAM极度受限（< 100字节），避免使用历史状态

### Optimization Tips / 优化建议

1. **Minimize transitions**: Fewer transitions = smaller table = faster search
   - **最小化转换**：更少的转换 = 更小的表 = 更快的搜索

2. **Use hierarchical states**: Reduce duplicate transitions via inheritance
   - **使用层级状态**：通过继承减少重复转换

3. **Avoid deep nesting**: Keep hierarchy shallow (2-3 levels max)
   - **避免深层嵌套**：保持层级浅（最多2-3层）

4. **Consider guard complexity**: Complex guards executed on every table scan
   - **考虑守卫复杂度**：每次表扫描都会执行复杂守卫

## Comparison with Other Algorithms / 与其他算法对比

| Aspect / 方面 | AlgoTable1 | Balanced1/Balanced2 |
|--------------|-----------|-------------------|
| **ROM usage** / **ROM使用** | **Low** (~30% of Balanced) | High (baseline) |
| **RAM usage** / **RAM使用** | **Very Low** | Low |
| **Transition speed** / **转换速度** | Moderate (linear search) | **Fast** (direct dispatch) |
| **Code complexity** / **代码复杂度** | **Simple** (single table) | Complex (many functions) |
| **Suitable for** / **适用于** | 2-8KB ROM, < 20 states | > 8KB ROM, any size |

## Limitations / 限制

### Current Limitations / 当前限制

1. **C99 transpiler only** - Other language support planned
   - **仅支持C99转译器** - 其他语言支持计划中

2. **No orthogonal regions** - Would cause transition table explosion
   - **不支持正交区域** - 会导致转换表爆炸

3. **Linear search performance** - Not suitable for very large state machines
   - **线性搜索性能** - 不适合超大型状态机

### Design Decisions / 设计决策

- **Pre-expansion strategy**: Hierarchical transitions are expanded at code-generation time, not runtime
  - **预展开策略**：层级转换在代码生成时展开，非运行时

- **Zero runtime overhead**: No dynamic memory allocation or function pointers
  - **零运行时开销**：无动态内存分配或函数指针

- **Table-driven dispatch**: All logic centralized in single dispatch function
  - **表驱动分发**：所有逻辑集中在单一分发函数中

## Migration Guide / 迁移指南

### From Balanced1/Balanced2 to Table1

**Step 1**: Update algorithm setting
```csharp
// Before:
algorithmId = AlgorithmId.Balanced2

// After:
algorithmId = AlgorithmId.Table1
transpilerId = TranspilerId.C99  // Required / 必需
```

**Step 3**: Verify performance
- Measure actual transition times in your target MCU
- Ensure response time meets your requirements
- 在目标MCU中测量实际转换时间
- 确保响应时间满足需求

### Compatibility Notes / 兼容性说明

- **State machine semantics unchanged**: Your diagram works identically
  - **状态机语义不变**：你的图表工作方式完全相同

- **Generated code structure different**: Don't rely on specific function names
  - **生成代码结构不同**：不要依赖特定函数名称

## Example Use Case / 使用案例

### Scenario / 场景

**Product**: Low-cost IoT temperature sensor
**产品**：低成本物联网温度传感器

**MCU**: ATtiny85 (8KB ROM, 512B RAM)

**Requirements** / **需求**:
- 8 states (idle, measuring, transmitting, sleep, etc.)
- 15 transitions total
- Response time: < 10ms acceptable
- 8个状态（空闲、测量、传输、睡眠等）
- 总计15个转换
- 响应时间：< 10ms可接受

**Result with AlgoTable1** / **使用AlgoTable1的结果**:
- ROM usage: ~1.2KB (vs 4KB with Balanced2)
- RAM usage: 4 bytes (state ID + variables)
- Average transition time: ~200µs @ 8MHz
- **ROM使用**：约1.2KB（vs Balanced2的4KB）
- **RAM使用**：4字节（状态ID + 变量）
- **平均转换时间**：约200微秒 @ 8MHz

**Conclusion** / **结论**: Perfect fit! Saved 2.8KB ROM for other features.
**结论**：完美适配！节省2.8KB ROM用于其他功能。

## Support & Contributing / 支持与贡献

- **Issues**: https://github.com/StateSmith/StateSmith/issues
- **Wiki**: https://github.com/StateSmith/StateSmith/wiki/Algorithms
- **Discussions**: https://github.com/StateSmith/StateSmith/discussions

For feature requests or bug reports related to AlgoTable1, please mention "Table1" in the issue title.

关于AlgoTable1的功能请求或错误报告，请在问题标题中提及"Table1"。

---

**Last updated**: 2025-12-29
**Version**: 1.0.0
