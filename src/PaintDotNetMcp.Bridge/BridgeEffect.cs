using System.Drawing;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.PropertySystem;

namespace PaintDotNetMcp.Bridge;

// Effect plugin entry point.
//
// What it does:
//   - First time the user invokes Effects > Tools > "MCP Bridge", BridgeServer starts a background
//     Named Pipe server. The server outlives any single Effect invocation because it's a static
//     thread tied to the Paint.NET process.
//   - The Effect itself is a no-op render: it copies source to destination unchanged. We use it
//     as a "start the server" trigger and as a way for the MCP server to request pixel mutations
//     on the active layer (queued and applied on the next render pass).
//
// Limitations (be honest):
//   - Direct document/layer manipulation outside of an Effect render pass is NOT part of the public
//     Paint.NET API. We can only safely mutate pixels through the Effect surface during render.
//   - A "fill" or "draw_rect" command from MCP is queued; it applies on the next render, i.e. when
//     the user invokes the effect.

public sealed class PluginSupportInfo : IPluginSupportInfo
{
    public string DisplayName => "Paint.NET MCP Bridge";
    public string Author => "paintdotnet-mcp";
    public string Copyright => "MIT";
    public Version Version => typeof(PluginSupportInfo).Assembly.GetName().Version ?? new Version(0, 1, 0, 0);
    public Uri WebsiteUri => new("https://github.com/");
}

[EffectCategory(EffectCategory.Effect)]
public sealed class BridgeEffect : PropertyBasedEffect
{
    public const string StaticName = "MCP Bridge";

    public BridgeEffect()
        : base(StaticName, "Tools", new EffectOptions { Flags = EffectFlags.None })
    {
        // Start the background pipe server on first construction. Idempotent.
        BridgeServer.EnsureStarted(this);
    }

    protected override PropertyCollection OnCreatePropertyCollection()
        => new PropertyCollection(Array.Empty<Property>());

    protected override void OnRender(Rectangle[] renderRects, int startIndex, int length)
    {
        // Hand the live render context to the server so it can apply queued mutations.
        BridgeServer.OnRenderPass(this, SrcArgs, DstArgs, renderRects, startIndex, length);
    }
}
