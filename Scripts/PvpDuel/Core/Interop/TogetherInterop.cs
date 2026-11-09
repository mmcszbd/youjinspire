using STS2RitsuLib.Interop;

namespace PvpDuel.Core.Interop;

/// <summary>
/// together 的接口存根：进决斗前把可能存在的共生体绑定解掉。
/// </summary>
/// <remarks>
/// 只声明本 mod 用到的那一个成员，签名与 together 0.3.1 的 <c>Together.Core.Api.TogetherApi</c> 逐字一致。
/// 清单里<b>故意不写</b> together 依赖：ModInterop 在目标 mod 没装时只是不转发（存根保持原样返回默认值、
/// 不抛异常），而工坊上的 together 还停在 0.2.0，写 <c>min_version: 0.3.0</c> 会让订阅者加载期直接失败。
/// </remarks>
[ModInterop("together", "Together.Core.Api.TogetherApi")]
internal static class TogetherInterop
{
    /// <summary>本局解除绑定：断开共享、按奇偶拆卡组，本局不再自动配对；本来没绑定时返回 false。</summary>
    public static bool Unbind(string reason = "external") => default;
}
