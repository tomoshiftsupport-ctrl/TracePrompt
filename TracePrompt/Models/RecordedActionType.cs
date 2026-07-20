namespace TracePrompt.Models;

/// <summary>
/// 記録したユーザー操作の種別です。
/// </summary>
public enum RecordedActionType
{
    LeftClick,
    RightClick,
    LeftDoubleClick,
    RightDoubleClick,
    MouseWheel,
    Drag,
    Keyboard
}
