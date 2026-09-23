#nullable enable
using Godot;

/// <summary>
/// 自测开关的读写。
///
/// 用法：在项目根目录放一个文本文件 <c>selftest.flag</c>，**内容写要测哪一个**：
/// <list type="bullet">
/// <item><c>home</c> —— 首页自测（校验两张卡片 + 真实点击卡片换场景）</item>
/// <item><c>sticker</c> —— 贴纸游戏自测</item>
/// <item><c>thunder</c> —— 雷霆战机自测</item>
/// <item><c>sheep</c> —— 羊了个羊自测</item>
/// </list>
/// 首页（主场景）读这个文件，按内容把游戏直接切过去；每个游戏只认自己那一个词。
/// 跑完自测会自己退出进程。
///
/// 导出版里 <c>res://</c> 是只读的、也不会打包 <c>.flag</c>，所以这个开关只在本机调试有效。
/// </summary>
public static class SelftestFlag
{
	public const string Path = "res://selftest.flag";

	/// <summary>
	/// flag 内容 = 这三个词之一，决定跑哪一套自测。
	/// 没有这个文件、或内容不是这三个词，则一切照常（正常启动，不进自测）——
	/// 「删掉开关 = 正常玩」比「删掉开关 = 跑首页自测」更符合直觉，也不会让 F5 直接退出。
	/// </summary>
	public const string TokenHome = "home";
	public const string TokenSticker = "sticker";
	public const string TokenThunder = "thunder";
	public const string TokenSheep = "sheep";

	/// <summary>返回开关文件的内容（去掉首尾空白并转小写）；文件不存在则返回空串。</summary>
	public static string Read()
	{
		if (!FileAccess.FileExists(Path))
			return "";
		return FileAccess.GetFileAsString(Path).Trim().ToLowerInvariant();
	}

	/// <summary>开关文件的原始内容（用于日志），不存在返回 "(none)"。</summary>
	public static string Describe()
	{
		string v = Read();
		return v.Length == 0 ? "(none)" : v;
	}

	public static bool Exists() => FileAccess.FileExists(Path);
}
