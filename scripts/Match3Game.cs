#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 消消乐（首页卡片里的「消除 · 三连」）。
///
/// 玩法：8×8 的糖果盘，点两个相邻格子交换位置。只要横竖任意一条线上连出 3 个
/// 及以上同款糖果，就整条消掉，上面的糖果塌下来、顶上再补新的（会连锁，连锁越深
/// 得分倍数越高）。每关给固定的步数，在步数用完前拿到目标分就算过关。
///
/// 难度按 5 关递增（见 <see cref="Levels"/>）：
/// <list type="bullet">
/// <item>目标分越来越高；</item>
/// <item>给的步数越来越少；</item>
/// <item>糖果种类从 5 种加到 6 种（种类越多越难凑齐）。</item>
/// </list>
///
/// 几个关键设计：
/// <list type="bullet">
/// <item>
/// <b>只有「能消」的交换才扣步数</b>：换过去凑不出三连的，原地弹回去、不扣步，
/// 这样玩家可以放心地试探（见 <see cref="TrySwap"/>）。
/// </item>
/// <item>
/// <b>状态机 + 可强制推进</b>：换位 / 消除 / 塌落三段动画都走 <see cref="Advance"/>，
/// 自测里用 <see cref="Settle"/> 一次性把动画推到底，就能对着最终盘面做断言，
/// 不用靠 sleep 碰运气。
/// </item>
/// <item>
/// <b>死盘自动洗牌</b>：塌完之后如果整盘都没有能凑出三连的交换，就重排现有糖果
/// （见 <see cref="Reshuffle"/>），避免玩家卡死。
/// </item>
/// </list>
///
/// 结构沿用全项目约定：一个脚本 + 一个瘦场景，图形全部 _Draw 现画，不依赖任何素材文件。
/// </summary>
public partial class Match3Game : Control, IResumable
{
	// ===================== 可调参数 =====================

	private const int Cols = 8;
	private const int Rows = 8;

	// 设计分辨率（project.godot 里配的 720×1280）。
	private const float DesignW = 720f;
	private const float DesignH = 1280f;

	private const float PadX = 16f;            // 棋盘左右留白
	private const float BoardTop = 236f;       // 棋盘可用区上边界（让开顶部 HUD）
	private const float BoardBottom = 946f;    // 棋盘可用区下边界
	private const float MinTile = 36f;
	private const float MaxTile = 96f;
	private const float FrameMargin = 22f;     // 画框 / 裁剪区比棋盘大出来的那圈

	// 三段动画的时长（秒）
	private const float SwapDur = 0.14f;
	private const float ClearDur = 0.22f;
	private const float FallDur = 0.20f;

	private const int BaseScore = 30;   // 每消一个糖果的基础分（再乘连击倍数）
	private const int CascadeCap = 60;  // 连击上限，防止极端情况下无限连锁

	private enum Phase { Intro, Idle, Swap, Clear, Fall, Won, Lost, AllClear }

	/// <summary>一关的难度参数。</summary>
	private sealed class Level
	{
		public int Target;   // 目标分
		public int Moves;    // 步数上限
		public int Kinds;    // 糖果种类数
	}

	private static readonly Level[] Levels =
	{
		new Level { Target = 900,  Moves = 30, Kinds = 5 },
		new Level { Target = 1400, Moves = 26, Kinds = 5 },
		new Level { Target = 2000, Moves = 24, Kinds = 6 },
		new Level { Target = 2700, Moves = 22, Kinds = 6 },
		new Level { Target = 3500, Moves = 20, Kinds = 6 },
	};

	// 6 种糖果：颜色 + 形状都不一样，形状也能认出来，不只靠颜色。
	private static readonly Color[] KindColors =
	{
		new Color("#ff5c6e"), new Color("#ff9c3c"), new Color("#ffd23f"),
		new Color("#6fe07a"), new Color("#4fd2ff"), new Color("#b98cff"),
	};

	// ===================== 状态 =====================

	private int[,] _cells = new int[Rows, Cols];      // 每格的糖果种类，-1 = 空
	private bool[,] _clearing = new bool[Rows, Cols]; // 这一轮正在被消掉的格子
	private int[,] _fallFrom = new int[Rows, Cols];   // 塌落动画：这一格的糖果从哪一行掉下来
	private const int NoFall = int.MinValue;

	private Phase _phase = Phase.Intro;
	private int _level;      // 当前关卡（0 基）
	private int _score;
	private int _moves;      // 剩余步数
	private int _combo;      // 本次连锁的倍数（每塌一轮 +1）
	private int _totalScore; // 通关时的累计分
	private float _phaseT;   // 当前阶段已经过去的时间
	private float _phaseDur;

	private Vector2I _swapA, _swapB;   // 正在播放换位动画的两格
	private bool _swapValid;           // 这次交换是不是有效（无效只做「弹回去」）

	private Vector2I? _selected;       // 点选的格子
	private Vector2I? _pressCell;      // 按下时落在的格子（抬手落在相邻格就交换）

	// 几何
	private float _tile = 70f;
	private Vector2 _origin;                                  // 棋盘左上角（Stage 坐标）
	private Vector2 _gridPad = new Vector2(FrameMargin, FrameMargin); // 画布内的棋盘左上角
	private float StageW => Size.X > 0f ? Size.X : DesignW;

	private int _seed;   // 0 = 按时间随机；自测里固定成一个值好复现
	private System.Random _rng = new System.Random();

	// ===================== 子节点 =====================

	private Control _stage = null!;
	private TextureRect _background = null!;
	private Node2D _board = null!;
	private Control _grid = null!;
	private Control _ui = null!;
	private HBoxContainer _topBar = null!;
	private Button _homeButton = null!;
	private Button _restartButton = null!;
	private Label _scoreLabel = null!;
	private Label _movesLabel = null!;
	private Label _hintLabel = null!;

	private ColorRect _overlay = null!;
	private Label _ovTitle = null!;
	private Label _ovInfo = null!;
	private Label _ovLine = null!;
	private Button _ovPrimary = null!;
	private Button _ovSecondary = null!;

	private StyleBoxFlat _sbFrame = null!;
	private StyleBoxFlat _sbFelt = null!;
	private StyleBoxFlat _sbCell = null!;

	// ===================== 生命周期 =====================

	public override void _Ready()
	{
		_stage = GetNode<Control>("Stage");
		_background = GetNode<TextureRect>("Stage/Background");
		_board = GetNode<Node2D>("Stage/Board");
		_ui = GetNode<Control>("UI");
		_topBar = GetNode<HBoxContainer>("UI/TopBar");
		_homeButton = GetNode<Button>("UI/TopBar/HomeButton");
		_restartButton = GetNode<Button>("UI/TopBar/RestartButton");

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		// 根节点铺满全屏，默认 MouseFilter = Stop 会「认领」落在空白处的点击，
		// 按钮 / 棋盘都收不到事件，所以必须让路。
		MouseFilter = MouseFilterEnum.Ignore;
		Theme = GameArt.MakeUiTheme();

		_background.Texture = GameArt.VerticalGradient(new Color("#1a1140"), new Color("#3a1f52"));
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
		_background.MouseFilter = MouseFilterEnum.Ignore;

		BuildStyleBoxes();
		BuildTopBar();
		BuildHud();
		BuildOverlay();

		// 棋盘是一块独立的裁剪画布：糖果从盘子上方掉进来时会被裁掉，
		// 不会糊到顶部 HUD 上。MouseFilter = Ignore，别把点击吃掉。
		_grid = new BoardView(this)
		{
			ClipContents = true,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		_board.AddChild(_grid);

		Layout();
		GetViewport().SizeChanged += Layout;

		_level = 0;
		_totalScore = 0;
		StartLevel(showIntro: true);

		GD.Print($"[Match3] ready. selftest={SelftestFlag.Describe()}");

		if (SelftestFlag.Read() == SelftestFlag.TokenMatch3)
		{
			_ = RunSelfTestAsync();
			return;
		}

		// 上次玩到一半被系统杀掉 → 把棋盘、分数、剩余步数接回来
		if (ResumeState.TryRestore(this, out var saved))
			Restore(saved);
	}

	// ===================== 续玩：快照 / 恢复 =====================

	/// <summary>
	/// 存盘面（每格的糖果种类，-1 = 空）、分数、剩余步数、累计分和关卡。
	/// <b>已经通关 / 失败的不存</b> —— 那种局面「接回来」只会让人莫名其妙。
	/// </summary>
	public Godot.Collections.Dictionary Snapshot()
	{
		if (_phase == Phase.Won || _phase == Phase.Lost)
			return new Godot.Collections.Dictionary();

		var cells = new Godot.Collections.Array();
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				cells.Add(_cells[r, c]);

		return new Godot.Collections.Dictionary
		{
			{ "level", _level },
			{ "cells", cells },
			{ "score", _score },
			{ "moves", _moves },
			{ "total", _totalScore },
		};
	}

	public void Restore(Godot.Collections.Dictionary data)
	{
		// 先按关卡正常铺一局（尺寸、目标分、HUD 都靠它），再把盘面覆盖回去
		_level = Mathf.Clamp(ResumeState.Int(data, "level", 0), 0, Levels.Length - 1);
		StartLevel(showIntro: false);

		var cells = ResumeState.Arr(data, "cells");
		if (cells.Count == Rows * Cols)
		{
			for (int r = 0; r < Rows; r++)
				for (int c = 0; c < Cols; c++)
					_cells[r, c] = ResumeState.Int(cells[r * Cols + c]);
		}

		_score = ResumeState.Int(data, "score");
		_moves = Mathf.Max(0, ResumeState.Int(data, "moves", _moves));
		_totalScore = ResumeState.Int(data, "total");

		_phase = Phase.Idle;
		_selected = null;
		_pressCell = null;
		_overlay.Visible = false;
		Redraw();
		UpdateHud();

		GD.Print($"[Match3] resumed: level={_level + 1} score={_score} moves={_moves}");
	}

	private void Layout()
	{
		// Control 不会自动铺满父节点：尺寸是 0 的话里面的东西全塌缩成一列看不见的
		_stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_stage.MouseFilter = MouseFilterEnum.Ignore;
		_ui.MouseFilter = MouseFilterEnum.Ignore;

		// 背景必须显式铺满 + 关掉「最小尺寸 = 纹理尺寸」，否则只在左上角画一小块。
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_background.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;

		ComputeGrid();
	}

	/// <summary>按行列数算格子边长和棋盘原点：取「宽高两个方向都放得下」的较小值，棋盘自然居中。</summary>
	private void ComputeGrid()
	{
		float availW = StageW - PadX * 2f;
		float availH = BoardBottom - BoardTop;
		float tile = Mathf.Min(availW / Cols, availH / Rows);
		_tile = Mathf.Clamp(Mathf.Floor(tile), MinTile, MaxTile);

		_origin = new Vector2(
			(StageW - _tile * Cols) * 0.5f,
			BoardTop + (availH - _tile * Rows) * 0.5f);

		_grid.Position = _origin - new Vector2(FrameMargin, FrameMargin);
		_grid.Size = new Vector2(_tile * Cols + FrameMargin * 2f, _tile * Rows + FrameMargin * 2f);
	}

	private void BuildStyleBoxes()
	{
		_sbFrame = GameArt.MakeBox(new Color("#1b2350"), 24, new Color("#0b1030"));
		_sbFelt = GameArt.MakeBox(new Color("#141d43"), 16, new Color("#0a0f28"));
		_sbCell = GameArt.MakeBox(new Color(1f, 1f, 1f, 0.045f), 8);
	}

	// ===================== 建界面 =====================

	private void BuildTopBar()
	{
		_topBar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_topBar.OffsetLeft = 20;
		_topBar.OffsetTop = 20;
		_topBar.OffsetRight = -20;
		_topBar.OffsetBottom = 96;
		_topBar.AddThemeConstantOverride("separation", 14);

		StyleBarButton(_homeButton, "返回", new Color("#8f7bff"));
		_homeButton.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);

		StyleBarButton(_restartButton, "重开", new Color("#ff8f6b"));
		_restartButton.Pressed += () =>
		{
			PlayPop(_restartButton);
			StartLevel(showIntro: false);
		};
	}

	private static void StyleBarButton(Button b, string text, Color bg)
	{
		GameArt.StyleButton(b, bg, Colors.White, fontSize: 30);
		b.CustomMinimumSize = new Vector2(130, 72);
		b.Text = text;
		b.FocusMode = FocusModeEnum.None;
	}

	private void BuildHud()
	{
		_scoreLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
		GameArt.OutlineText(_scoreLabel, new Color("#ffd77a"), 38, 8);
		_scoreLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_scoreLabel.GrowHorizontal = GrowDirection.Both;
		_scoreLabel.OffsetTop = 102;
		_scoreLabel.OffsetBottom = 158;
		_ui.AddChild(_scoreLabel);

		_movesLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
		_movesLabel.AddThemeFontSizeOverride("font_size", 26);
		_movesLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.72f));
		_movesLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_movesLabel.GrowHorizontal = GrowDirection.Both;
		_movesLabel.OffsetTop = 162;
		_movesLabel.OffsetBottom = 206;
		_ui.AddChild(_movesLabel);

		_hintLabel = new Label
		{
			Text = "点两个相邻糖果交换 · 三个以上连成一线就消除",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		_hintLabel.AddThemeFontSizeOverride("font_size", 22);
		_hintLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.5f));
		_hintLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		_hintLabel.GrowHorizontal = GrowDirection.Both;
		_hintLabel.GrowVertical = GrowDirection.Begin;
		_hintLabel.OffsetTop = -98;
		_hintLabel.OffsetBottom = -54;
		_ui.AddChild(_hintLabel);
	}

	private void BuildOverlay()
	{
		_overlay = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.62f),
			MouseFilter = MouseFilterEnum.Stop,
			Visible = false,
		};
		_overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.AddChild(_overlay);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(0.09f, 0.11f, 0.20f, 0.98f), 32, new Color(1f, 1f, 1f, 0.20f));
		box.ContentMarginLeft = box.ContentMarginRight = 40;
		box.ContentMarginTop = box.ContentMarginBottom = 32;
		panel.AddThemeStyleboxOverride("panel", box);
		_overlay.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 16);
		panel.AddChild(col);

		_ovTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(_ovTitle, Colors.White, 54, 8);
		col.AddChild(_ovTitle);

		_ovInfo = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_ovInfo.AddThemeFontSizeOverride("font_size", 30);
		_ovInfo.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.86f));
		col.AddChild(_ovInfo);

		_ovLine = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_ovLine.AddThemeFontSizeOverride("font_size", 28);
		_ovLine.AddThemeColorOverride("font_color", new Color("#ffd77a"));
		col.AddChild(_ovLine);

		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		row.AddThemeConstantOverride("separation", 16);
		col.AddChild(row);

		_ovPrimary = MakeOverlayButton("开始", new Color("#ff8f6b"));
		_ovPrimary.Pressed += () =>
		{
			PlayPop(_ovPrimary);
			if (_phase == Phase.Intro)
				BeginPlay();
			else if (_phase == Phase.Won)
			{
				_level++;
				StartLevel(showIntro: false);
			}
			else if (_phase == Phase.AllClear)
			{
				_level = 0;
				_totalScore = 0;
				StartLevel(showIntro: false);
			}
			else if (_phase == Phase.Lost)
				StartLevel(showIntro: false);
		};
		row.AddChild(_ovPrimary);

		_ovSecondary = MakeOverlayButton("回首页", new Color("#8f7bff"));
		_ovSecondary.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);
		row.AddChild(_ovSecondary);
	}

	private static Button MakeOverlayButton(string text, Color bg)
	{
		var b = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(200, 96),
			FocusMode = FocusModeEnum.None,
		};
		GameArt.StyleButton(b, bg, Colors.White, fontSize: 30);
		return b;
	}

	private void ShowOverlay(string title, string info, string line, string primary, string? secondary)
	{
		_ovTitle.Text = title;
		_ovInfo.Text = info;
		_ovLine.Text = line;
		_ovLine.Visible = line.Length > 0;
		_ovPrimary.Text = primary;
		_ovSecondary.Visible = secondary != null;
		if (secondary != null)
			_ovSecondary.Text = secondary;
		_overlay.Visible = true;
	}

	private void PlayPop(Control c)
	{
		if (!IsInstanceValid(c) || c.Size.X <= 0f)
			return;
		c.PivotOffset = c.Size * 0.5f;
		var tw = CreateTween();
		tw.TweenProperty(c, "scale", new Vector2(0.94f, 0.94f), 0.06);
		tw.TweenProperty(c, "scale", Vector2.One, 0.18)
			.SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
	}

	// ===================== 一局的开始与结束 =====================

	private void NewRng() =>
		_rng = new System.Random(_seed != 0 ? _seed : (int)(Time.GetTicksMsec() & 0x7FFFFFFF));

	/// <summary>重置分数 / 步数，重新铺一盘，并按 <paramref name="showIntro"/> 决定是弹关卡说明还是直接开玩。</summary>
	private void StartLevel(bool showIntro)
	{
		NewRng();
		_score = 0;
		_combo = 0;
		_moves = Levels[_level].Moves;
		_selected = null;
		_pressCell = null;
		_clearing = new bool[Rows, Cols];
		_fallFrom = new int[Rows, Cols];
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				_fallFrom[r, c] = NoFall;

		FillBoard();
		UpdateHud();

		if (showIntro)
		{
			_phase = Phase.Intro;
			var lv = Levels[_level];
			ShowOverlay("消消乐", $"第 {_level + 1} 关 / {Levels.Length} 关",
				$"目标 {lv.Target} 分 · {lv.Moves} 步 · {lv.Kinds} 种糖果", "开始", "回首页");
		}
		else
		{
			_phase = Phase.Idle;
			_overlay.Visible = false;
		}
		Redraw();
	}

	private void BeginPlay()
	{
		_overlay.Visible = false;
		_phase = Phase.Idle;
		_selected = null;
		Redraw();
	}

	private void UpdateHud()
	{
		var lv = Levels[_level];
		_scoreLabel.Text = $"第 {_level + 1} 关 · {_score} / {lv.Target}";
		string m = $"剩余 {Mathf.Max(0, _moves)} 步";
		if (_combo >= 2)
			m += $"   ·   连击 x{_combo}";
		_movesLabel.Text = m;
	}

	private void Redraw()
	{
		if (IsInstanceValid(_grid))
			_grid.QueueRedraw();
	}

	// ===================== 盘面生成 =====================

	private int KindsNow => Levels[_level].Kinds;

	/// <summary>
	/// 生成盘面：先铺一盘「本身没有三连」的（<see cref="PickSafe"/> 保证），
	/// 再确保至少存在一个能凑出三连的交换。
	/// </summary>
	private void FillBoard()
	{
		FillFresh();
		for (int i = 0; i < 200 && !HasPossibleMove(); i++)
			ReshuffleOnce();
		if (!HasPossibleMove())
			FillFresh();
	}

	/// <summary>随机铺一盘，逐个挑「不会和左边两个 / 上面两个凑成三连」的种类，所以铺完一定没有三连。</summary>
	private void FillFresh()
	{
		int kinds = KindsNow;
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				_cells[r, c] = PickSafe(r, c, kinds);
	}

	private int PickSafe(int r, int c, int kinds)
	{
		var banned = new HashSet<int>();
		if (c >= 2 && _cells[r, c - 1] == _cells[r, c - 2])
			banned.Add(_cells[r, c - 1]);
		if (r >= 2 && _cells[r - 1, c] == _cells[r - 2, c])
			banned.Add(_cells[r - 1, c]);

		var ok = new List<int>(kinds);
		for (int k = 0; k < kinds; k++)
			if (!banned.Contains(k))
				ok.Add(k);
		return ok[_rng.Next(ok.Count)];
	}

	/// <summary>就地把现有糖果重排一次；重排结果「没有三连、且有可行交换」才算成功。</summary>
	private bool ReshuffleOnce()
	{
		var list = new List<int>(Rows * Cols);
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				if (_cells[r, c] >= 0)
					list.Add(_cells[r, c]);

		for (int i = list.Count - 1; i > 0; i--)
		{
			int j = _rng.Next(i + 1);
			(list[i], list[j]) = (list[j], list[i]);
		}

		int n = 0;
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				if (_cells[r, c] >= 0)
					_cells[r, c] = list[n++];

		return !HasMatch() && HasPossibleMove();
	}

	/// <summary>死盘救援：重排现有糖果；极端情况下（比如整盘只剩两种颜色，怎么排都没救）直接重铺一盘。</summary>
	private void Reshuffle()
	{
		for (int i = 0; i < 300; i++)
			if (ReshuffleOnce())
				return;
		for (int i = 0; i < 300; i++)
		{
			FillFresh();
			if (HasPossibleMove())
				return;
		}
	}

	// ===================== 规则 =====================

	private static bool Adjacent(Vector2I a, Vector2I b) =>
		Mathf.Abs(a.X - b.X) + Mathf.Abs(a.Y - b.Y) == 1;

	private static bool InBounds(Vector2I v) =>
		v.X >= 0 && v.Y >= 0 && v.X < Cols && v.Y < Rows;

	/// <summary>整盘是否存在三连（只看「有没有」，图快，不建数组）。</summary>
	private bool HasMatch()
	{
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c + 2 < Cols; c++)
			{
				int k = _cells[r, c];
				if (k >= 0 && k == _cells[r, c + 1] && k == _cells[r, c + 2])
					return true;
			}
		for (int c = 0; c < Cols; c++)
			for (int r = 0; r + 2 < Rows; r++)
			{
				int k = _cells[r, c];
				if (k >= 0 && k == _cells[r + 1, c] && k == _cells[r + 2, c])
					return true;
			}
		return false;
	}

	/// <summary>标出所有该消掉的格子（横竖两个方向各扫一遍，交叉处取并集）。</summary>
	private bool[,] FindMatches()
	{
		var m = new bool[Rows, Cols];
		for (int r = 0; r < Rows; r++)
		{
			int c = 0;
			while (c < Cols)
			{
				int k = _cells[r, c];
				int c2 = c + 1;
				while (k >= 0 && c2 < Cols && _cells[r, c2] == k)
					c2++;
				if (k >= 0 && c2 - c >= 3)
					for (int x = c; x < c2; x++)
						m[r, x] = true;
				c = c2;
			}
		}
		for (int c = 0; c < Cols; c++)
		{
			int r = 0;
			while (r < Rows)
			{
				int k = _cells[r, c];
				int r2 = r + 1;
				while (k >= 0 && r2 < Rows && _cells[r2, c] == k)
					r2++;
				if (k >= 0 && r2 - r >= 3)
					for (int y = r; y < r2; y++)
						m[y, c] = true;
				r = r2;
			}
		}
		return m;
	}

	private static int CountMask(bool[,] m)
	{
		int n = 0;
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				if (m[r, c])
					n++;
		return n;
	}

	private void SwapData(Vector2I a, Vector2I b) =>
		(_cells[a.Y, a.X], _cells[b.Y, b.X]) = (_cells[b.Y, b.X], _cells[a.Y, a.X]);

	/// <summary>盘上是否还存在「换一下就能凑出三连」的相邻对。</summary>
	private bool HasPossibleMove()
	{
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
			{
				if (c + 1 < Cols && WouldMatchAfterSwap(new Vector2I(c, r), new Vector2I(c + 1, r)))
					return true;
				if (r + 1 < Rows && WouldMatchAfterSwap(new Vector2I(c, r), new Vector2I(c, r + 1)))
					return true;
			}
		return false;
	}

	private bool WouldMatchAfterSwap(Vector2I a, Vector2I b)
	{
		SwapData(a, b);
		bool ok = HasMatch();
		SwapData(a, b);
		return ok;
	}

	/// <summary>找一对能凑出三连的相邻格（自测用；正常玩由玩家自己点）。</summary>
	private (Vector2I A, Vector2I B)? FindValidSwap()
	{
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
			{
				if (c + 1 < Cols && WouldMatchAfterSwap(new Vector2I(c, r), new Vector2I(c + 1, r)))
					return (new Vector2I(c, r), new Vector2I(c + 1, r));
				if (r + 1 < Rows && WouldMatchAfterSwap(new Vector2I(c, r), new Vector2I(c, r + 1)))
					return (new Vector2I(c, r), new Vector2I(c, r + 1));
			}
		return null;
	}

	private bool AllFilled()
	{
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				if (_cells[r, c] < 0)
					return false;
		return true;
	}

	// ===================== 操作 =====================

	/// <summary>
	/// 尝试交换两格。凑不出三连的交换「原地弹回去、不扣步」，只有能消的才真正算一步。
	/// </summary>
	private bool TrySwap(Vector2I a, Vector2I b)
	{
		if (_phase != Phase.Idle || !Adjacent(a, b))
			return false;
		if (_cells[a.Y, a.X] < 0 || _cells[b.Y, b.X] < 0)
			return false;

		SwapData(a, b);          // 先换数据（动画就靠「数据已换、画面还在旧位」错开）
		_swapValid = HasMatch();
		if (!_swapValid)
			SwapData(a, b);      // 无效：数据当场还原，动画只做弹回

		_swapA = a;
		_swapB = b;
		_selected = null;
		_phase = Phase.Swap;
		_phaseT = 0f;
		_phaseDur = SwapDur;
		Redraw();
		return _swapValid;
	}

	/// <summary>棋盘点击。用 _UnhandledInput：根节点 MouseFilter = Ignore，事件能落到这里。</summary>
	public override void _UnhandledInput(InputEvent @event)
	{
		if (_overlay.Visible || _phase != Phase.Idle)
			return;
		if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left)
			return;

		var cell = CellAt(mb.Position);
		if (!InBounds(cell))
			return;

		if (mb.Pressed)
		{
			_pressCell = cell;
			return;
		}

		var press = _pressCell;
		_pressCell = null;
		if (press == null)
			return;

		if (press.Value == cell)
		{
			// 点同一格 = 选中 / 取消。再点一次相邻格就交换。
			_selected = _selected.HasValue && _selected.Value == cell ? null : cell;
			Redraw();
		}
		else if (Adjacent(press.Value, cell))
		{
			TrySwap(press.Value, cell);
		}
		else
		{
			_selected = cell;
			Redraw();
		}
	}

	private Vector2I CellAt(Vector2 p)
	{
		// p 是视口坐标；_grid 这块 Control 没旋转也没缩放，所以「相对画布的局部坐标」
		// 就是全局坐标减掉它自己的 GlobalPosition（Control 上没有 ToLocal）。
		var lp = (p - _grid.GlobalPosition) - _gridPad;
		return new Vector2I(
			Mathf.FloorToInt(lp.X / _tile),
			Mathf.FloorToInt(lp.Y / _tile));
	}

	// ===================== 主循环 =====================

	public override void _Process(double delta)
	{
		if (_phase != Phase.Swap && _phase != Phase.Clear && _phase != Phase.Fall)
			return;
		Advance((float)delta);
		Redraw();
	}

	/// <summary>推进当前动画阶段；到点了就切换阶段。自测用 <see cref="Settle"/> 传一个大 delta 一次推完。</summary>
	private void Advance(float delta)
	{
		if (_phase != Phase.Swap && _phase != Phase.Clear && _phase != Phase.Fall)
			return;
		_phaseT += delta;
		if (_phaseT >= _phaseDur)
		{
			_phaseT = _phaseDur;
			EndPhase();
		}
	}

	private float T => _phaseDur <= 0f ? 1f : Mathf.Clamp(_phaseT / _phaseDur, 0f, 1f);

	private void EndPhase()
	{
		switch (_phase)
		{
			case Phase.Swap:
				if (_swapValid)
				{
					_moves--;
					_combo = 0;
					UpdateHud();
					StartClear();
				}
				else
				{
					_phase = Phase.Idle;
					Redraw();
				}
				break;

			case Phase.Clear:
				for (int r = 0; r < Rows; r++)
					for (int c = 0; c < Cols; c++)
						if (_clearing[r, c])
							_cells[r, c] = -1;
				_clearing = new bool[Rows, Cols];
				ComputeFall();
				_phase = Phase.Fall;
				_phaseT = 0f;
				_phaseDur = FallDur;
				Redraw();
				break;

			case Phase.Fall:
				_fallFrom = new int[Rows, Cols];
				for (int r = 0; r < Rows; r++)
					for (int c = 0; c < Cols; c++)
						_fallFrom[r, c] = NoFall;
				StartClear();   // 塌落之后可能又凑出三连 → 继续连锁
				break;
		}
	}

	/// <summary>结算一轮消除：有得消就进入消除阶段，没得消就收尾。</summary>
	private void StartClear()
	{
		var mask = FindMatches();
		int n = CountMask(mask);
		if (n == 0)
		{
			AfterResolve();
			return;
		}

		if (_combo < CascadeCap)
			_combo++;
		_score += n * BaseScore * _combo;
		_clearing = mask;
		_phase = Phase.Clear;
		_phaseT = 0f;
		_phaseDur = ClearDur;
		UpdateHud();
		Redraw();
	}

	/// <summary>塌落：每列的糖果落到底，顶上补新糖（新糖的「来处」是负数行，动画从盘子上方掉进来）。</summary>
	private void ComputeFall()
	{
		int kinds = KindsNow;
		_fallFrom = new int[Rows, Cols];
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				_fallFrom[r, c] = NoFall;

		for (int c = 0; c < Cols; c++)
		{
			int write = Rows - 1;
			for (int r = Rows - 1; r >= 0; r--)
			{
				if (_cells[r, c] < 0)
					continue;
				if (write != r)
				{
					_cells[write, c] = _cells[r, c];
					_cells[r, c] = -1;
					_fallFrom[write, c] = r;
				}
				write--;
			}
			for (int r = write; r >= 0; r--)
			{
				_cells[r, c] = _rng.Next(kinds);
				_fallFrom[r, c] = r - (write + 1);   // 从盘子上方第 -1、-2… 行掉进来
			}
		}
	}

	/// <summary>一轮连锁结束、盘面稳定之后：判过关 / 判失败 / 死盘洗牌。</summary>
	private void AfterResolve()
	{
		_selected = null;
		var lv = Levels[_level];

		if (_score >= lv.Target)
		{
			_totalScore += _score;
			if (_level >= Levels.Length - 1)
			{
				_phase = Phase.AllClear;
				ShowOverlay("全部通关！", $"{Levels.Length} 关全部完成",
					$"第 {_level + 1} 关得分 {_score} · 累计 {_totalScore}", "再玩一次", null);
			}
			else
			{
				_phase = Phase.Won;
				ShowOverlay("过关！", $"第 {_level + 1} 关 · 得分 {_score}",
					$"目标 {lv.Target} 分 · 还剩 {Mathf.Max(0, _moves)} 步", "下一关", "回首页");
			}
			Redraw();
			return;
		}

		if (_moves <= 0)
		{
			_phase = Phase.Lost;
			ShowOverlay("差一点", $"第 {_level + 1} 关 · 得分 {_score} / {lv.Target}",
				"步数用完了，再来一次吧", "重试", "回首页");
			Redraw();
			return;
		}

		if (!HasPossibleMove())
			Reshuffle();   // 死盘：重排，别让玩家卡死

		_phase = Phase.Idle;
		UpdateHud();
		Redraw();
	}

	/// <summary>把正在播的动画一口气推到底（自测用：不靠 sleep，直接对着最终盘面断言）。</summary>
	private void Settle()
	{
		int guard = 0;
		while ((_phase == Phase.Swap || _phase == Phase.Clear || _phase == Phase.Fall) && guard++ < 400)
			Advance(1e6f);
	}

	// ===================== 绘制 =====================

	private Vector2 CellLocal(int c, int r) =>
		_gridPad + new Vector2((c + 0.5f) * _tile, (r + 0.5f) * _tile);

	private Rect2 CellRect(int c, int r) =>
		new(_gridPad + new Vector2(c * _tile + 2f, r * _tile + 2f), new Vector2(_tile - 4f, _tile - 4f));

	private void DrawBoard(CanvasItem ci)
	{
		float w = _tile * Cols, h = _tile * Rows;

		ci.DrawStyleBox(_sbFrame, new Rect2(_gridPad - new Vector2(6f, 6f), new Vector2(w + 12f, h + 12f)));
		ci.DrawStyleBox(_sbFelt, new Rect2(_gridPad, new Vector2(w, h)));

		// 淡棋盘格：给格子一个参照
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				if (((r + c) & 1) == 0)
					ci.DrawStyleBox(_sbCell, CellRect(c, r));

		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				DrawTile(ci, c, r);

		// 选中的那一格画个白框
		if (_selected.HasValue && _phase == Phase.Idle)
		{
			var s = _selected.Value;
			ci.DrawRect(CellRect(s.X, s.Y), new Color(1f, 1f, 1f, 0.85f), false, 4f);
		}
	}

	private void DrawTile(CanvasItem ci, int c, int r)
	{
		int k = _cells[r, c];
		if (k < 0)
			return;

		var center = CellLocal(c, r);
		float scale = 1f, alpha = 1f;
		float t = T;

		if (_phase == Phase.Clear && _clearing[r, c])
		{
			scale = 1f - 0.85f * t;
			alpha = 1f - t;
		}
		else if (_phase == Phase.Fall)
		{
			int f = _fallFrom[r, c];
			if (f != NoFall)
				center.Y += (f - r) * _tile * (1f - t);
		}
		else if (_phase == Phase.Swap)
		{
			if (t < 1f)
			{
				if (c == _swapA.X && r == _swapA.Y)
					center += (Vector2)(_swapB - _swapA) * _tile * (_swapValid ? 1f - t : Mathf.Sin(t * Mathf.Pi) * 0.45f);
				else if (c == _swapB.X && r == _swapB.Y)
					center += (Vector2)(_swapA - _swapB) * _tile * (_swapValid ? 1f - t : Mathf.Sin(t * Mathf.Pi) * 0.45f);
			}
		}

		DrawCandy(ci, center, _tile * 0.42f, k, scale, alpha);
	}

	/// <summary>画一颗糖果：6 种形状互相区分，颜色也各不相同。</summary>
	private void DrawCandy(CanvasItem ci, Vector2 center, float r, int kind, float scale, float alpha)
	{
		float rr = r * scale;
		if (rr <= 1f || alpha <= 0.02f)
			return;

		var fill = KindColors[Mathf.Clamp(kind, 0, KindColors.Length - 1)];
		var line = fill.Darkened(0.42f);
		var fc = new Color(fill.R, fill.G, fill.B, alpha);
		var lc = new Color(line.R, line.G, line.B, alpha);
		float lw = Mathf.Max(1.6f, rr * 0.16f);

		switch (kind)
		{
			case 0: // 圆
				ci.DrawCircle(center, rr, fc);
				ci.DrawArc(center, rr, 0f, Mathf.Tau, 26, lc, lw, true);
				ci.DrawCircle(center + new Vector2(-rr * 0.30f, -rr * 0.30f), rr * 0.20f, new Color(1f, 1f, 1f, 0.5f * alpha));
				break;

			case 1: // 方
				var box = new Rect2(center - Vector2.One * rr, Vector2.One * (rr * 2f));
				ci.DrawRect(box, fc);
				ci.DrawRect(box, lc, false, lw);
				break;

			case 2: // 星
				GameArt.Poly(ci, Star(center, rr), fc, lc, lw);
				break;

			case 3: // 三角
				GameArt.Poly(ci, new[]
				{
					center + new Vector2(0f, -rr),
					center + new Vector2(rr * 0.90f, rr * 0.70f),
					center + new Vector2(-rr * 0.90f, rr * 0.70f),
				}, fc, lc, lw);
				break;

			case 4: // 菱形
				GameArt.Poly(ci, new[]
				{
					center + new Vector2(0f, -rr),
					center + new Vector2(rr, 0f),
					center + new Vector2(0f, rr),
					center + new Vector2(-rr, 0f),
				}, fc, lc, lw);
				break;

			default: // 六边形
				GameArt.Poly(ci, Hex(center, rr), fc, lc, lw);
				break;
		}
	}

	private static Vector2[] Star(Vector2 c, float r)
	{
		var pts = new Vector2[10];
		for (int i = 0; i < 10; i++)
		{
			float a = -Mathf.Pi * 0.5f + i * Mathf.Pi / 5f;
			float rad = (i & 1) == 0 ? r : r * 0.46f;
			pts[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad;
		}
		return pts;
	}

	private static Vector2[] Hex(Vector2 c, float r)
	{
		var pts = new Vector2[6];
		for (int i = 0; i < 6; i++)
		{
			float a = -Mathf.Pi * 0.5f + i * Mathf.Tau / 6f;
			pts[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
		}
		return pts;
	}

	// ===================== 自测 =====================
	//
	// 触发方式：项目根目录放 selftest.flag，内容写 match3，然后启动游戏
	// （首页会立刻切到本场景，本场景看到内容是自己就跑这一套）。
	// 动画一律用 Settle() 推到底，断言对着最终盘面做，不靠 sleep。

	private async Task RunSelfTestAsync()
	{
		GD.Print("[SELFTEST] begin (match3)");
		try
		{
			await Wait(0.3);

			int fails = 0;
			_seed = 20261015;   // 固定随机，盘面每次一样，出问题好复现

			// ① 开局：铺满、无三连、有可行交换、分数 0、步数 = 关卡配置
			StartLevel(showIntro: false);
			bool startFull = AllFilled();
			bool startNoMatch = !HasMatch();
			bool startHasMove = HasPossibleMove();
			bool startMeta = _score == 0 && _moves == Levels[0].Moves && _phase == Phase.Idle;
			GD.Print($"[SELFTEST] 开局: 铺满={startFull} 无三连={startNoMatch} 有可行交换={startHasMove} " +
					 $"分={_score} 步={_moves}(应 {Levels[0].Moves}) -> {startFull && startNoMatch && startHasMove && startMeta}");
			if (!startFull || !startNoMatch || !startHasMove || !startMeta) fails++;

			// ② 找三连：横着摆三个 → 命中 3 格
			SetBaseBoard();
			_cells[0, 0] = 0; _cells[0, 1] = 0; _cells[0, 2] = 0;
			int hit = CountMask(FindMatches());
			GD.Print($"[SELFTEST] 找三连: 命中 {hit} 格(应 3) -> {hit == 3}");
			if (hit != 3) fails++;

			// ③ 无效交换：换过去凑不出三连 → 返回 false、数据弹回原样、不扣步
			SetBaseBoard();
			int movesB = _moves;
			bool invalidRet = TrySwap(new Vector2I(0, 0), new Vector2I(1, 0));
			Settle();
			bool invalidOk = !invalidRet && _cells[0, 0] == 0 && _cells[0, 1] == 1 &&
							 _moves == movesB && _phase == Phase.Idle;
			GD.Print($"[SELFTEST] 无效交换: 返回={invalidRet} (0,0)={_cells[0, 0]} (0,1)={_cells[0, 1]} " +
					 $"步={_moves}(应 {movesB}) -> {invalidOk}");
			if (!invalidOk) fails++;

			// ④ 有效交换：换一下凑出三连 → 扣 1 步、消掉 3 颗、盘面补齐且无残留三连
			SetBaseBoard();
			_cells[2, 0] = 0; _cells[2, 1] = 0; _cells[2, 2] = 1; _cells[3, 2] = 0;
			int movesC = _moves;
			bool validRet = TrySwap(new Vector2I(2, 2), new Vector2I(2, 3));
			Settle();
			bool validOk = validRet && _score >= 3 * BaseScore && _moves == movesC - 1 &&
						   AllFilled() && !HasMatch() && _phase == Phase.Idle;
			GD.Print($"[SELFTEST] 有效交换: 返回={validRet} 分={_score}(应 >= {3 * BaseScore}) " +
					 $"步={_moves}(应 {movesC - 1}) 铺满={AllFilled()} 无残留={!HasMatch()} -> {validOk}");
			if (!validOk) fails++;

			// ⑤ 打分与连击：3 消 = 3×30、4 消 = 4×30，连击倍数按 _combo 翻倍
			SetBaseBoard();
			_cells[0, 0] = 0; _cells[0, 1] = 0; _cells[0, 2] = 0;
			_combo = 0; _score = 0; _phase = Phase.Idle;
			StartClear();
			bool s3 = _score == 3 * BaseScore && _combo == 1 && _phase == Phase.Clear;
			SetBaseBoard();
			_cells[0, 0] = 1; _cells[0, 1] = 1; _cells[0, 2] = 1; _cells[0, 3] = 1;
			_combo = 0; _score = 0; _phase = Phase.Idle;
			StartClear();
			bool s4 = _score == 4 * BaseScore && _combo == 1;
			SetBaseBoard();
			_cells[1, 0] = 2; _cells[1, 1] = 2; _cells[1, 2] = 2;
			_combo = 1; _score = 0; _phase = Phase.Idle;
			StartClear();
			bool s2 = _score == 3 * BaseScore * 2 && _combo == 2;
			GD.Print($"[SELFTEST] 打分: 3消={s3} 4消={s4} 连击x2={s2} -> {s3 && s4 && s2}");
			if (!s3 || !s4 || !s2) fails++;

			// ⑥ 过关：分够目标 → 过关面板；点「下一关」→ 进第 2 关、分数归零、步数按新关卡配置
			StartLevel(showIntro: false);
			_moves = 5;
			_score = Levels[0].Target;
			_phase = Phase.Idle;
			AfterResolve();
			bool wonOk = _phase == Phase.Won && _overlay.Visible && _ovPrimary.Text == "下一关";
			_ovPrimary.EmitSignal(BaseButton.SignalName.Pressed);
			bool nextOk = _level == 1 && _score == 0 && _moves == Levels[1].Moves && _phase == Phase.Idle;
			GD.Print($"[SELFTEST] 过关: 面板={wonOk} 下一关={nextOk} (关={_level} 步={_moves}) -> {wonOk && nextOk}");
			if (!wonOk || !nextOk) fails++;

			// ⑦ 失败：步数只剩 1，交换一次后用光步数且分不够 → 失败面板；点「重试」→ 本关重开
			_level = 0;
			StartLevel(showIntro: false);
			_moves = 1;
			_score = 0;
			var mv = FindValidSwap();
			bool swapped = mv != null && TrySwap(mv.Value.A, mv.Value.B);
			Settle();
			bool lostOk = swapped && _moves == 0 && _score < Levels[0].Target &&
						  _phase == Phase.Lost && _overlay.Visible && _ovPrimary.Text == "重试";
			_ovPrimary.EmitSignal(BaseButton.SignalName.Pressed);
			bool retryOk = _score == 0 && _moves == Levels[0].Moves && _phase == Phase.Idle;
			GD.Print($"[SELFTEST] 失败: 交换={swapped} 步={_moves} 面板={lostOk} 重试={retryOk} -> {lostOk && retryOk}");
			if (!lostOk || !retryOk) fails++;

			// ⑧ 死盘洗牌：对角斜纹铺满（种类 = (列+行) % 5）—— 行列都相邻差 1、同值相隔 5 格，
			//     本身无三连，且任何单次相邻交换都凑不出三连，是真正的死盘。
			//     洗牌后必须变成「有可行交换、无三连、铺满」。
			for (int r = 0; r < Rows; r++)
				for (int c = 0; c < Cols; c++)
					_cells[r, c] = (c + r) % 5;
			bool deadBefore = !HasPossibleMove();
			Reshuffle();
			bool aliveOk = deadBefore && !HasMatch() && HasPossibleMove() && AllFilled();
			GD.Print($"[SELFTEST] 死盘洗牌: 洗牌前无解={deadBefore} 洗牌后无三连={!HasMatch()} " +
					 $"有可行交换={HasPossibleMove()} 铺满={AllFilled()} -> {aliveOk}");
			if (!aliveOk) fails++;

			// ⑨ 布局：格子装得进可用区、棋盘落在上下边界之内
			bool gridOk = _tile >= MinTile &&
						  _tile * Cols <= StageW - PadX * 2f + 0.5f &&
						  _origin.Y >= BoardTop - 0.5f &&
						  _origin.Y + _tile * Rows <= BoardBottom + 0.5f;
			bool clipOk = _grid.Size.X >= _tile * Cols && _grid.Size.Y >= _tile * Rows;
			GD.Print($"[SELFTEST] 布局: tile={_tile} origin={_origin} 画布={_grid.Size} -> {gridOk && clipOk}");
			if (!gridOk || !clipOk) fails++;

			// ⑩ 背景真的铺满了吗（露出 0.3 灰的清屏色就说明背景没覆盖整屏）
			var shot = GetViewport().GetTexture().GetImage();
			var px = new Vector2I((int)(shot.GetWidth() * 0.03f), (int)(shot.GetHeight() * 0.5f));
			var col = shot.GetPixel(px.X, px.Y);
			bool bgOk = !GameArt.IsClearColor(col);
			GD.Print($"[SELFTEST] background covers screen: pixel{px}={col} -> {bgOk}");
			if (!bgOk) fails++;

			// ⑪ 画面里真有糖果：抓起始格子的像素，看有没有「非背景、非底板」的彩色像素
			StartLevel(showIntro: false);
			await Wait(0.2);
			var bshot = GetViewport().GetTexture().GetImage();
			float kx = bshot.GetWidth() / DesignW;
			float ky = bshot.GetHeight() / DesignH;
			int colorful = 0, total = 0;
			for (int r = 0; r < Rows; r++)
				for (int c = 0; c < Cols; c++)
				{
					var center = _grid.GlobalPosition + CellLocal(c, r);
					var pc = bshot.GetPixel((int)(center.X * kx), (int)(center.Y * ky));
					total++;
					// 棋盘底色是深蓝（R/B 都偏低），糖果是饱和亮色：R 明显大于底色即可
					if (pc.R > 0.35f || pc.G > 0.5f)
						colorful++;
				}
			bool candiesOk = colorful >= Rows * Cols / 2;
			GD.Print($"[SELFTEST] 糖果可见: 彩色格 {colorful}/{total} -> {candiesOk}");
			if (!candiesOk) fails++;

			// ⑫ 真实 GUI 链路：点一下某格，必须被选中（事件真的送到了棋盘）
			StartLevel(showIntro: false);
			var target = new Vector2I(3, 4);
			var clickPos = _grid.GlobalPosition + CellLocal(target.X, target.Y);
			PushGuiClick(clickPos);
			await Wait(0.1);
			bool guiOk = _selected.HasValue && _selected.Value == target;
			GD.Print($"[SELFTEST] gui 点格 {target}: 选中={_selected} -> {guiOk}");
			if (!guiOk) fails++;

			// ⑬ 截一张中局盘面（先走两步，画面里有消除过的痕迹）
			StartLevel(showIntro: false);
			for (int i = 0; i < 2; i++)
			{
				var m = FindValidSwap();
				if (m != null)
				{
					TrySwap(m.Value.A, m.Value.B);
					Settle();
				}
			}
			await Wait(0.2);
			SaveShot("match3_board");

			GD.Print(fails == 0 ? "[SELFTEST] PASSED" : $"[SELFTEST] FAILED ({fails} 项)");
			await Wait(0.4);
			GetTree().Quit();
		}
		catch (System.Exception e)
		{
			// 自测是 fire-and-forget 协程，异常会被 Task 静默吞掉（表现为日志戛然而止、进程挂着不退出）
			GD.Print($"[SELFTEST] CRASHED: {e}");
			GetTree().Quit(1);
		}
	}

	// ---------- 自测用的小工具 ----------

	/// <summary>
	/// 一盘「本身没有三连」的底图：种类取 (c + 2r) % 5。
	/// 横向相邻差 1、纵向相邻差 2，同一行/列里连不出三个一样的，方便在上面精确地摆出想测的形态。
	/// </summary>
	private void SetBaseBoard()
	{
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				_cells[r, c] = (c + 2 * r) % 5;
	}

	/// <summary>把一次点击推给视口，走引擎的 GUI 命中测试（能测出「事件根本没送到棋盘」）。</summary>
	private void PushGuiClick(Vector2 pos)
	{
		var vp = GetViewport();
		vp.PushInput(new InputEventMouseButton
		{
			Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left,
			Pressed = true, Device = 0,
		}, true);
		vp.PushInput(new InputEventMouseButton
		{
			Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left,
			Pressed = false, Device = 0,
		}, true);
	}

	private string SaveShot(string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		string path = $"user://screenshots/{tag}_{stamp}.png";
		var err = img.SavePng(path);
		GD.Print($"[Match3] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}

	// ===================== 内嵌节点 =====================
	//
	// 注意：C# 里凡是继承 Godot 节点类型的类（哪怕是嵌套的私有类）都必须写 partial，
	// 否则编译直接报 GD0001。

	/// <summary>棋盘画布（带裁剪）。逻辑都在 Match3Game 里，它只负责把 _Draw 转发过去。</summary>
	private sealed partial class BoardView : Control
	{
		private readonly Match3Game _game;

		public BoardView(Match3Game game)
		{
			_game = game;
		}

		public override void _Draw() => _game.DrawBoard(this);
	}
}
