#nullable enable

/// <summary>
/// 所有场景路径的唯一出处。
/// 以前路径是散在各个脚本里的裸字符串（比如「返回首页」直接在代码里写 "res://scenes/Home.tscn"），
/// 加了第二个游戏之后再这么干就容易改一处漏一处，所以集中到这里。
/// </summary>
public static class ScenePaths
{
	public const string Home = "res://scenes/Home.tscn";
	public const string StickerGame = "res://scenes/StickerGame.tscn";
	public const string ThunderGame = "res://scenes/ThunderGame.tscn";
	public const string SheepGame = "res://scenes/SheepGame.tscn";
}
