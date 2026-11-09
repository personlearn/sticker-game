#nullable enable
using Godot;
using System;
using System.Collections.Generic;

/// <summary>残局的过关条件。</summary>
public enum PuzzleGoal
{
	/// <summary>提掉指定数量的白子（黑棋提子数达标）。适合「打吃 → 提子」类训练。</summary>
	Capture,

	/// <summary>把一组关键交叉点全部占成黑子。适合「连接 / 做眼 / 补断点」类训练。</summary>
	KeyPoints,
}

/// <summary>
/// 一道残局。棋盘坐标一律是 <c>x,y</c>（从左上角 0 开始），用分号分隔，
/// 例如 <c>"3,4;5,4;4,3"</c>。<see cref="Keys"/> 既是答案点，也是电脑的「不许抢」区域。
/// </summary>
public sealed class PuzzleDef
{
	public string Name = "";     // 关卡名
	public string Grade = "";    // 「一步 / 两步 / 多步」，只用于面板显示
	public string Goal = "";     // 一句话目标，游戏里一直挂在副标题上
	public string Tip = "";      // 思路提示，不给具体坐标
	public int Size;             // 9 / 13 / 19
	public string Black = "";    // 初始黑子
	public string White = "";    // 初始白子
	public string Keys = "";     // 答案点（也是 AI 禁区的一部分）
	public PuzzleGoal Kind;
	public int Target;           // Capture：要提掉几颗白子
	public int MaxMoves;         // 玩家可用手数，超了就提示再试（仍可继续对战数子）
	public Color Tint = Colors.White;
}

/// <summary>
/// 围棋残局题库。
///
/// <para>
/// 设计约定（改题之前先看这几条）：
/// <list type="number">
/// <item><b>初始局面必须合法</b>：每一块棋至少还有一口气。被打吃（只剩 1 气）是允许的，
///       那正是「一手提子」题的来源；零气的棋块不能摆在盘上。</item>
/// <item><b>答案点必须是空的</b>，而且落在「所有初始棋子 + 答案点」外扩一格的局部区域里。</item>
/// <item><b>电脑会避开这个局部区域</b>：它照常在别处布局、照常应手，但不抢答案点、
///       也不去救那块被打吃的白棋。否则多步题会被机器人随手破坏，小孩根本练不成。</item>
/// <item>残局不是摆着看的棋谱：达成目标给「过关」反馈，之后可以一直下到双方停一手数子，
///       也就是「解题」和「对战」两条完成路径都留着。</item>
/// </list>
/// </para>
/// </summary>
public static class GoPuzzles
{
	/// <summary>按「路数从小到大、每档从一步到多步」排列，面板顺序就是它。</summary>
	public static readonly PuzzleDef[] All =
	{
		// ---------- 9 路：一眼能看懂的入门题 ----------
		new PuzzleDef
		{
			Name = "一手提子", Grade = "一步", Size = 9,
			Goal = "提掉中间那颗白子",
			Tip = "白棋只剩一口气，把最后那口气堵上",
			Black = "3,4;5,4;4,3", White = "4,4", Keys = "4,5",
			Kind = PuzzleGoal.Capture, Target = 1, MaxMoves = 1,
			Tint = new Color("#7ee787"),
		},
		new PuzzleDef
		{
			Name = "左右开弓", Grade = "两步", Size = 9,
			Goal = "把左右两颗白子都提掉",
			Tip = "两处各剩一口气，提完一边别忘了另一边",
			Black = "1,2;3,2;2,1;5,2;7,2;6,1", White = "2,2;6,2", Keys = "2,3;6,3",
			Kind = PuzzleGoal.Capture, Target = 2, MaxMoves = 3,
			Tint = new Color("#8fd3ff"),
		},

		// ---------- 13 路：连接与断点 ----------
		new PuzzleDef
		{
			Name = "接回同伴", Grade = "一步", Size = 13,
			Goal = "把两块黑棋连成一块",
			Tip = "白棋想从中间切断，你先把断点补上",
			Black = "5,5;7,5", White = "6,4;6,6;5,6;7,6", Keys = "6,5",
			Kind = PuzzleGoal.KeyPoints, MaxMoves = 1,
			Tint = new Color("#7ee787"),
		},
		new PuzzleDef
		{
			Name = "追着吃", Grade = "多步", Size = 13,
			Goal = "提掉两颗被打吃的白子",
			Tip = "先在左下收气，再回头收右上那口",
			Black = "2,8;4,8;3,7;8,3;10,3;9,2", White = "3,8;9,3", Keys = "3,9;9,4",
			Kind = PuzzleGoal.Capture, Target = 2, MaxMoves = 4,
			Tint = new Color("#ffb46b"),
		},

		// ---------- 19 路：大棋盘上的要点与多点连接 ----------
		new PuzzleDef
		{
			Name = "点住要点", Grade = "一步", Size = 19,
			Goal = "在要点落子，别让白棋切断",
			Tip = "两块黑棋之间只差一子",
			Black = "8,8;10,8", White = "9,7;9,9;8,9;10,9", Keys = "9,8",
			Kind = PuzzleGoal.KeyPoints, MaxMoves = 1,
			Tint = new Color("#7ee787"),
		},
		new PuzzleDef
		{
			Name = "两只眼位", Grade = "多步", Size = 19,
			Goal = "占住两个要点，把三段黑棋连成一片",
			Tip = "两个断点都要补，只补一个还连不上",
			Black = "5,5;7,5;9,5", White = "6,4;8,4;6,6;8,6", Keys = "6,5;8,5",
			Kind = PuzzleGoal.KeyPoints, MaxMoves = 3,
			Tint = new Color("#ffb46b"),
		},
		new PuzzleDef
		{
			Name = "三处收气", Grade = "多步", Size = 19,
			Goal = "提掉三块只剩一口气的白棋",
			Tip = "按顺序把三口气一个个堵上",
			Black = "4,5;6,5;5,4;7,5;9,5;8,4;10,5;12,5;11,4",
			White = "5,5;8,5;11,5", Keys = "5,6;8,6;11,6",
			Kind = PuzzleGoal.Capture, Target = 3, MaxMoves = 5,
			Tint = new Color("#ff8f6b"),
		},
	};

	/// <summary>路数 → 难度表下标（贴目跟着难度走）。</summary>
	public static int LevelFor(int size) => size switch
	{
		9 => 0,
		13 => 1,
		_ => 2,
	};

	/// <summary>把 "x,y;x,y" 解析成一维棋盘下标；越界或格式不对的点直接丢掉。</summary>
	public static int[] Points(string spec, int n)
	{
		var list = new List<int>();
		foreach (string part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			string[] xy = part.Split(',');
			if (xy.Length != 2)
				continue;
			if (!int.TryParse(xy[0].Trim(), out int x) || !int.TryParse(xy[1].Trim(), out int y))
				continue;
			if ((uint)x >= (uint)n || (uint)y >= (uint)n)
				continue;
			list.Add(y * n + x);
		}
		return list.ToArray();
	}
}
