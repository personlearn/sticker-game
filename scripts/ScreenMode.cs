#nullable enable
using Godot;

/// <summary>
/// 屏幕方向（竖版 / 横版）的唯一出处。
///
/// <para>
/// 为什么需要这个东西：整个游戏是按**竖版 720×1280** 设计的。
/// 手机上一切正常；但平板的最小宽度 ≥ 600dp，从 Android 12L 起系统会「忽略应用的方向锁定」，
/// app 被顶成**横屏全屏**，Godot 只能把竖版画面居中 letterbox —— 左右两条巨大的黑框。
/// （Android 16 / targetSdk 36 把这条规则扩张到了所有大屏设备；官方给游戏类 app 留了豁免，
///   但各厂商平板的「应用全屏 / 宽高比」设置仍然可能把 app 顶成横屏。）
/// </para>
///
/// <para>
/// 这里做两件事：
/// <list type="number">
/// <item>运行时把方向**请求**给系统（<see cref="DisplayServer.ScreenSetOrientation"/>）——
///       系统接受的话 app 会真的转过来，竖版画面自然铺满；</item>
/// <item>把选择记到 <c>user://display.cfg</c>，下次启动自动套用，不用每次都点。</item>
/// </list>
/// </para>
///
/// <para>
/// 桌面上 <see cref="DisplayServer.ScreenSetOrientation"/> 是空操作，
/// 为了能在电脑上验证，桌面端改成「把窗口掰成对应比例」——
/// 掰成横的之后 letterbox 立刻就能看见，正好复现平板上的样子。
/// </para>
/// </summary>
public static class ScreenMode
{
	private const string ConfigPath = "user://display.cfg";
	private const string Section = "screen";
	private const string KeyLandscape = "landscape";

	/// <summary>竖版窗口的像素尺寸（和 project.godot 的 window_*_override 保持一致）。</summary>
	private static readonly Vector2I PortraitWindow = new(472, 840);

	/// <summary>横版窗口的像素尺寸（就是竖版的宽高对调）。</summary>
	private static readonly Vector2I LandscapeWindow = new(840, 472);

	/// <summary>
	/// 当前是不是横版。默认竖版 —— 和 <c>project.godot</c> 里的
	/// <c>window/handheld/orientation=1</c>（Portrait）保持一致。
	/// </summary>
	public static bool IsLandscape { get; private set; }

	/// <summary>只有手机上才真的有「屏幕方向」可转。</summary>
	private static bool IsMobile => OS.HasFeature("mobile");

	/// <summary>当前方向叫什么。</summary>
	public static string CurrentName => IsLandscape ? "横屏" : "竖屏";

	/// <summary>点一下会切到哪一边。</summary>
	public static string NextName => IsLandscape ? "竖屏" : "横屏";

	/// <summary>从磁盘读回上次的选择。只读文件，不动系统状态（应用请调 <see cref="Apply"/>）。</summary>
	public static void Load()
	{
		var cfg = new ConfigFile();
		if (cfg.Load(ConfigPath) == Error.Ok)
			IsLandscape = cfg.GetValue(Section, KeyLandscape, false).AsBool();
		else
			IsLandscape = false; // 没有配置文件 = 第一次跑 = 竖版
	}

	/// <summary>翻到另一边：落盘 + 立刻生效。</summary>
	public static void Toggle()
	{
		IsLandscape = !IsLandscape;
		Save();
		Apply();
	}

	/// <summary>把当前方向真正应用到系统。</summary>
	public static void Apply()
	{
		if (IsMobile)
		{
			DisplayServer.ScreenSetOrientation(IsLandscape
				? DisplayServer.ScreenOrientation.Landscape
				: DisplayServer.ScreenOrientation.Portrait);
			return;
		}

		// 桌面端的 display server 不支持转屏，硬调只会换回一句
		// 「WARNING: Orientation not supported by this display server.」加一坨 C# 堆栈。
		// 所以这里干脆不调，改用窗口形状来模拟，好让本机也能看见效果。
		ApplyDesktopWindow();
	}

	private static void Save()
	{
		var cfg = new ConfigFile();
		cfg.Load(ConfigPath); // 先读回来，别把以后加的键冲掉
		cfg.SetValue(Section, KeyLandscape, IsLandscape);
		var err = cfg.Save(ConfigPath);
		if (err != Error.Ok)
			GD.PushWarning($"[ScreenMode] save {ConfigPath} failed: {err}");
	}

	/// <summary>
	/// 桌面端没法转屏幕，就换个窗口形状来模拟。
	/// 横版时窗口变扁，Godot 的 letterbox 立刻可见 —— 正好是平板上那两条黑框的样子。
	/// </summary>
	private static void ApplyDesktopWindow()
	{
		if (Engine.GetMainLoop() is not SceneTree tree)
			return;
		var win = tree.Root;
		if (win == null)
			return;

		var size = IsLandscape ? LandscapeWindow : PortraitWindow;
		if (win.Size == size)
			return;
		win.Size = size;
	}
}
