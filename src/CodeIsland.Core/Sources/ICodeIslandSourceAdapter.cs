namespace CodeIsland.Core.Sources;

/// <summary>
/// Source adapter 的第一阶段只承载低风险纯函数差异：
/// 来源元数据、事件名标准化和 permission 响应风格。
/// 后续可继续扩展到 source 识别、安装和能力声明。
/// </summary>
public interface ICodeIslandSourceAdapter
{
    string SourceKey { get; }

    string DisplayName { get; }

    string IconName { get; }

    CodeIslandPermissionResponseStyle PermissionResponseStyle { get; }

    bool TryNormalizeEventName(string rawEventName, out string normalizedEventName);
}
