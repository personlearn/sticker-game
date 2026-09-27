#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 俄罗斯方块（首页卡片里的「方块 · 消行」）。
///
/// 玩法：10×20 的井里不断落下七种四格方块，左右移动、旋转、加速下坠，
/// 把整行填满就消掉并得分；方块堆到顶就结束。每消 10 行升一级，落得越来越快。
///
/// 两个设计上的关键点：
/// <list type="bullet">
/// <item>
/// <b>七种方块都用方阵存，旋转就是矩阵转置</b>：I 用 4×4、其余用 3×3、O 用 2×2，
/// 顺时针旋转 90° = <c>new[r][c] = old[n-1-c][r]</c>（见 <see cref="RotateCw"/>）。
/// 这样「四个朝向」不用手写 28 张表，也不会写错某一个朝向。
/// I 必须在 4×4 里转、T 之类必须在 3×3 里转，否则旋转中心会偏、方块转起来会「飘」。
/// </item>
/// <item>
/// <b>出块用七袋（7-bag）而不是纯随机</b>：纯随机可能连出五个 S 一个 I 都不给，
/// 玩家会连续被憋死。<see cref="NextFromBag"/> 把七种洗好牌依次发完再洗下一轮，
/// 保证任意七个连续方块里七种各一个。
/// </item>
/// </list>
///
/// 结构沿用全项目约定：一个脚本 + 一个瘦场景，图形全部 _Draw 现画，不依赖任何素材文件。
/// </summary>
public partial class TetrisGame : Control
{
	// ===================== 可调参数 =====================

	private const int Cols = 10;
	private const int Rows = 20;

	// 设计分辨率（project.godot 里配的 720×1280）。窗口实际像素是另一回事，
	// 自测要把逻辑坐标换算成截图像素时得用它。
	private const float DesignW = 720f;
	private const float DesignH = 1280f;

	private const float PadX = 10f;
	private const float BoardTop = 150f;
	private const float BoardBottom = 930f;
	private const float MinTile = 18f;
	private const float MaxTile = 42f;

	private const float BaseGravity = 0.72f; // 一级时每下落一行的秒数
	private const float MinGravity = 0.07f;
	private const float GravityDecay = 0.80f; // 每升一级乘这个系数

	private const float PopLife = 0.34f;    // 消行的白闪时长
	private const float ShakeDecay = 30f;   // 消行 / 结束的震屏衰减（px/s）

	// 底部按键的几何（Stage 绝对坐标）：四个操作键一行 + 一个「直落到底」
	private const float PadTop = 946f;
	private const float PadH = 104f;
	private const float PadUnit = 150f;
	private const float PadLeft = 30f;
	private const float DropTop = 1062f;
	private const float DropH = 96f;

	private const string BestPath = "user://tetris_best.cfg";

	// ===================== 方块定义 =====================

	/// <summary>
	/// 一种方块。只存**一个基准朝向**，另外三个朝向由 <see cref="RotateCw"/> 算出来。
	/// <c>Cells</c> 是边长 <c>Size</c> 的方阵，1 = 有方块。
	/// </summary>
	private sealed class PieceDef
	{
		public string Name = "";
		public Color Tint = Colors.White;
		public int[,] Cells = new int[0, 0];
	}

	private static readonly PieceDef[] Pieces =
	{
		new PieceDef { Name = "I", Tint = new Color("#4fd2ff"), Cells = Mat(4, "0000", "1111", "0000", "0000") },
		new PieceDef { Name = "O", Tint = new Color("#ffd23f"), Cells = Mat(2, "11", "11") },
		new PieceDef { Name = "T", Tint = new Color("#b98cff"), Cells = Mat(3, "010", "111", "000") },
		new PieceDef { Name = "S", Tint = new Color("#6fe07a"), Cells = Mat(3, "011", "110", "000") },
		new PieceDef { Name = "Z", Tint = new Color("#ff5c6e"), Cells = Mat(3, "110", "011", "000") },
		new PieceDef { Name = "J", Tint = new Color("#5b8cff"), Cells = Mat(3, "100", "111", "000") },
		new PieceDef { Name = "L", Tint = new Color("#ff9c3c"), Cells = Mat(3, "001", "111", "000") },
	};

	private static int[,] Mat(int n, params string[] rows)
	{
		var m = new int[n, n];
		for (int r = 0; r < n; r++)
			for (int c = 0; c < n; c++)
				m[r, c] = rows[r][c] == '1' ? 1 : 0;
		return m;
	}

	/// <summary>顺时针旋转 90°：<c>new[r][c] = old[n-1-c][r]</c>（四方阵通用，O 转完还是自己）。</summary>
	private static int[,] RotateCw(int[,] m)
	{
		int n = m.GetLength(0);
		var o = new int[n, n];
		for (int r = 0; r < n; r++)
			for (int c = 0; c < n; c++)
				o[r, c] = m[n - 1 - c, r];
		return o;
	}

	/// <summary>墙踢的候选偏移：先原地，再左右各 1、2 格，最后往上让一格。</summary>
	private static readonly Vector2I[] KickOffsets =
	{
		new(0, 0), new(-1, 0), new(1, 0), new(-2, 0), new(2, 0), new(0, -1),
	};

	// ===================== 状态 =====================

	private enum Phase { Play, Paused, Over }

	private int[,] _grid = new int[Rows, Cols]; // 0 = 空，1~7 = 已固定的方块颜色（下标 +1）

	private int[,] _piece = new int[0, 0]; // 当前方块（已旋转后的形状）
	private int _pieceColor;               // 1~7，对应 Pieces[] 下标 + 1
	private Vector2I _piecePos;            // 方块矩阵左上角在井里的位置

	private int _nextIndex;                // 下一块的类型下标
	private readonly List<int> _bag = new(); // 七袋发牌器

	private Phase _phase = Phase.Play;
	private int _score;
	private int _lines;
	private int _level = 1;
	private float _gravity = BaseGravity;
	private float _acc;
	private float _shake;
	private double _now;

	private int _seed; // 0 = 按时间随机；自测里固定成一个值好复现
	private System.Random _rng = new();

	private int _best; // 最高分，0 = 还没玩过

	private sealed class Pop { public int Row; public float Age; }
	private readonly List<Pop> _pops = new();

	// ===================== 子节点 =====================

	private Control _stage = null!;
	private TextureRect _background = null!;
	private Node2D _board = null!;
	private Control _ui = null!;
	private HBoxContainer _topBar = null!;
	private Button _homeButton = null!;
	private Button _restartButton = null!;
	private Button _pauseButton = null!;

	private Button _btnLeft = null!;
	private Button _btnRight = null!;
	private Button _btnDown = null!;
	private Button _btnRotate = null!;
	private Button _btnDrop = null!;

	private Label _scoreLabel = null!;
	private Label _hintLabel = null!;

	private ColorRect _overlay = null!;
	private Label _ovTitle = null!;
	private Label _ovInfo = null!;
	private Label _ovLine = null!;
	private Button _ovPrimary = null!;
	private Button _ovSecondary = null!;
	private bool _ovIsPause;

	private GridView _gridView = null!;
	private Font? _font;

	// HUD 文本缓存：只在数值真的变了才重建字符串
	private int _hudScore = int.MinValue, _hudLevel = int.MinValue, _hudLines = int.MinValue;

	// 画棋盘用的样式盒（缓存起来，_Draw 里不能每格 new 一个）
	private StyleBoxFlat _sbFrame = null!;
	private StyleBoxFlat _sbFelt = null!;
	private StyleBoxFlat _sbCellLine = null!;
	private StyleBoxFlat _sbBlockEdge = null!;
	private StyleBoxFlat _sbBlock = null!;
	private StyleBoxFlat _sbPanel = null!;
	private StyleBoxFlat _sbFlash = null!;

	private float _tile = 39f;
	private Vector2 _origin;

	private float StageW => Size.X > 0f ? Size.X : DesignW;

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
		_pauseButton = GetNode<Button>("UI/TopBar/PauseButton");

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		// 根节点铺满全屏，默认的 MouseFilter = Stop 会「认领」所有落在空白处的点击，
		// 把事件从引擎的 GUI 流程里截走。所以根节点必须让路，按钮才收得到点击。
		MouseFilter = MouseFilterEnum.Ignore;
		Theme = GameArt.MakeUiTheme();
		_font = GameArt.UiFont;

		// 背景：深蓝 → 靛紫，夜里玩不刺眼
		_background.Texture = GameArt.VerticalGradient(new Color("#0b1030"), new Color("#2b1f4a"));
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
		_background.MouseFilter = MouseFilterEnum.Ignore;

		BuildStyleBoxes();
		BuildTopBar();
		BuildHud();
		BuildPad();
		BuildOverlay();
		LoadBest();

		// 棋盘是一块独立画布。注意 QueueRedraw 必须打在**真正重写了 _Draw 的那个节点**上。
		_gridView = new GridView(this);
		_board.AddChild(_gridView);

		Layout();
		GetViewport().SizeChanged += Layout;

		GD.Print($"[Tetris] ready. best={_best} selftest={SelftestFlag.Describe()}");

		// 先开局：_Process 的落块逻辑假定「当前方块」一定存在，自测开头要 await 一会儿，
		// 空着会当场空引用，所以自测也必须从「已开局」的状态起跑。
		StartGame();

		if (SelftestFlag.Read() == SelftestFlag.TokenTetris)
			_ = RunSelfTestAsync();
	}

	private void Layout()
	{
		// Control 不会自动铺满父节点：尺寸是 0 的话里面的东西全塌缩成一列看不见的
		_stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_stage.MouseFilter = MouseFilterEnum.Ignore;
		_ui.MouseFilter = MouseFilterEnum.Ignore;

		// 背景必须显式铺满 + 关掉「最小尺寸 = 纹理尺寸」，否则只在左上角画一小块、
		// 其余露出视口清屏色（0.3 灰）。
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_background.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;

		ComputeGrid();
	}

	/// <summary>按行列数算格子边长和井的原点：取宽高都放得下的较小值，井自然居中。</summary>
	private void ComputeGrid()
	{
		float availW = StageW - PadX * 2f;
		float availH = BoardBottom - BoardTop;
		float tile = Mathf.Min(availW / Cols, availH / Rows);
		_tile = Mathf.Clamp(Mathf.Floor(tile), MinTile, MaxTile);

		_origin = new Vector2(
			(StageW - _tile * Cols) * 0.5f,
			BoardTop + (availH - _tile * Rows) * 0.5f);
		_gridView.Position = _origin;
		LayoutPad();
	}

	private void BuildStyleBoxes()
	{
		_sbFrame = GameArt.MakeBox(new Color("#141a3d"), 20, new Color("#07091c"));
		_sbFelt = GameArt.MakeBox(new Color("#0a0f2a"), 12, new Color("#232c5c"));
		_sbCellLine = GameArt.MakeBox(new Color(1f, 1f, 1f, 0.05f), 5);
		_sbBlockEdge = GameArt.MakeBox(new Color("#05070f"), 6);
		_sbBlock = GameArt.MakeBox(Colors.White, 5);
		_sbPanel = GameArt.MakeBox(new Color(1f, 1f, 1f, 0.07f), 14, new Color(1f, 1f, 1f, 0.18f));
		_sbFlash = GameArt.MakeBox(Colors.White, 4);
	}

	// ===================== 一局的开始与结束 =====================

	private void StartGame()
	{
		// 注意别写 Environment.TickCount：这个文件里有 using Godot，
		// 那个名字会解析到 Godot.Environment（3D 环境资源）上去，编译直接报错。
		_rng = new System.Random(_seed != 0 ? _seed : (int)(Time.GetTicksMsec() & 0x7FFFFFFF));

		ClearGrid();
		_pops.Clear();
		_bag.Clear();
		_score = 0;
		_lines = 0;
		_level = 1;
		_gravity = BaseGravity;
		_acc = 0f;
		_shake = 0f;

		_nextIndex = NextFromBag();
		SpawnPiece();

		_phase = Phase.Play;
		_overlay.Visible = false;
		_board.Position = Vector2.Zero;

		_hudScore = _hudLevel = _hudLines = int.MinValue;
		UpdateHud();
		Redraw();

		GD.Print($"[Tetris] start. 首块={Pieces[_pieceColor - 1].Name} 尺寸={_piece.GetLength(0)} " +
				 $"位置={_piecePos} 重力={_gravity:0.###}s");
	}

	private void ClearGrid()
	{
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				_grid[r, c] = 0;
	}

	/// <summary>七袋发牌：把七种洗好依次发完再洗下一轮，任意七个连续方块里七种各一个。</summary>
	private int NextFromBag()
	{
		if (_bag.Count == 0)
		{
			for (int i = 0; i < Pieces.Length; i++)
				_bag.Add(i);
			for (int i = _bag.Count - 1; i > 0; i--)
			{
				int j = _rng.Next(i + 1);
				(_bag[i], _bag[j]) = (_bag[j], _bag[i]);
			}
		}
		int k = _bag[0];
		_bag.RemoveAt(0);
		return k;
	}

	/// <summary>把 <see cref="_nextIndex"/> 那一块放到井口，然后抽下一块。</summary>
	private void SpawnPiece()
	{
		var def = Pieces[_nextIndex];
		_piece = Clone(def.Cells);
		_pieceColor = _nextIndex + 1;

		_nextIndex = NextFromBag();

		int n = _piece.GetLength(0);
		_piecePos = new Vector2I((Cols - n) / 2, 0);

		if (Collides(_piece, _piecePos))
		{
			GD.Print("[Tetris] 井口被堵住了");
			GameOver();
		}
	}

	private static int[,] Clone(int[,] m) => (int[,])m.Clone();

	private void GameOver()
	{
		if (_phase == Phase.Over)
			return;

		_phase = Phase.Over;
		_shake = 12f;
		bool record = _score > _best;
		if (record)
		{
			_best = _score;
			SaveBest();
		}

		ShowOverlay("堆到顶了", $"得分 {_score} · 消行 {_lines}", $"最高 {_best}", false);
		GD.Print($"[Tetris] over. score={_score} lines={_lines} best={_best}");
		Redraw();
	}

	// ===================== 规则 =====================

	/// <summary>这块方块摆在 <paramref name="pos"/> 会不会撞（越出左右下边界或压到已固定的方块）。</summary>
	private bool Collides(int[,] m, Vector2I pos)
	{
		int n = m.GetLength(0);
		for (int r = 0; r < n; r++)
		{
			for (int c = 0; c < n; c++)
			{
				if (m[r, c] == 0)
					continue;
				int bc = pos.X + c;
				int br = pos.Y + r;
				if (bc < 0 || bc >= Cols || br >= Rows)
					return true;
				if (br >= 0 && _grid[br, bc] != 0)
					return true;
			}
		}
		return false;
	}

	private bool Move(int dx, int dy)
	{
		var np = new Vector2I(_piecePos.X + dx, _piecePos.Y + dy);
		if (Collides(_piece, np))
			return false;
		_piecePos = np;
		return true;
	}

	/// <summary>旋转。原地转不动就按 <see cref="KickOffsets"/> 依次试，能塞下就转。</summary>
	private bool Rotate()
	{
		var rot = RotateCw(_piece);
		foreach (var off in KickOffsets)
		{
			var np = _piecePos + off;
			if (!Collides(rot, np))
			{
				_piece = rot;
				_piecePos = np;
				return true;
			}
		}
		return false;
	}

	/// <summary>下落一行；下不动就地锁定（返回 false）。</summary>
	private bool MoveDown()
	{
		if (Move(0, 1))
			return true;
		Lock();
		return false;
	}

	/// <summary>直接落到底，按落下的格数给分。</summary>
	private void HardDrop()
	{
		int n = 0;
		while (!Collides(_piece, new Vector2I(_piecePos.X, _piecePos.Y + n + 1)))
			n++;
		_piecePos = new Vector2I(_piecePos.X, _piecePos.Y + n);
		_score += n * 2;
		UpdateHud();
		Lock();
	}

	/// <summary>把当前方块写进井里，然后消行、出下一块。</summary>
	private void Lock()
	{
		int n = _piece.GetLength(0);
		for (int r = 0; r < n; r++)
			for (int c = 0; c < n; c++)
				if (_piece[r, c] != 0)
				{
					int bc = _piecePos.X + c;
					int br = _piecePos.Y + r;
					if (br >= 0 && br < Rows && bc >= 0 && bc < Cols)
						_grid[br, bc] = _pieceColor;
				}

		int cleared = ClearLines();

		if (_phase == Phase.Play)
			SpawnPiece();

		if (cleared > 0)
			Redraw();
	}

	/// <summary>消掉填满的行，从下往上把上面的行落下来。返回消掉的行数。</summary>
	private int ClearLines()
	{
		var full = new List<int>();
		for (int r = 0; r < Rows; r++)
		{
			bool all = true;
			for (int c = 0; c < Cols; c++)
				if (_grid[r, c] == 0)
				{
					all = false;
					break;
				}
			if (all)
				full.Add(r);
		}

		if (full.Count == 0)
			return 0;

		// 从下往上压实：非满行依次落到写指针处，最后把顶部残留清掉
		int write = Rows - 1;
		for (int r = Rows - 1; r >= 0; r--)
		{
			if (full.Contains(r))
				continue;
			if (write != r)
				for (int c = 0; c < Cols; c++)
				{
					_grid[write, c] = _grid[r, c];
					_grid[r, c] = 0;
				}
			write--;
		}
		for (int r = 0; r <= write; r++)
			for (int c = 0; c < Cols; c++)
				_grid[r, c] = 0;

		// 四行一次 1000 分，是「一次消一行 × 4」的 2.5 倍 —— 鼓励攒着一次消
		int[] table = { 0, 100, 300, 600, 1000 };
		int gained = table[Mathf.Min(full.Count, 4)] * _level;
		_score += gained;
		_lines += full.Count;
		_level = 1 + _lines / 10;
		_gravity = Mathf.Max(MinGravity, BaseGravity * Mathf.Pow(GravityDecay, _level - 1));

		foreach (int r in full)
			_pops.Add(new Pop { Row = r, Age = 0f });
		_shake = full.Count >= 3 ? 10f : 5f;

		UpdateHud();
		GD.Print($"[Tetris] 消 {full.Count} 行 · 行数={_lines} 等级={_level} 得分={_score} 重力={_gravity:0.###}s");
		return full.Count;
	}

	// ===================== 主循环 =====================

	public override void _Process(double delta)
	{
		_now += delta;
		bool anim = false;

		for (int i = _pops.Count - 1; i >= 0; i--)
		{
			_pops[i].Age += (float)delta;
			anim = true;
			if (_pops[i].Age >= PopLife)
				_pops.RemoveAt(i);
		}

		if (_shake > 0f)
		{
			_shake = Mathf.Max(0f, _shake - ShakeDecay * (float)delta);
			_board.Position = new Vector2(
				(float)(_rng.NextDouble() * 2.0 - 1.0) * _shake,
				(float)(_rng.NextDouble() * 2.0 - 1.0) * _shake);
			anim = true;
			if (_shake <= 0f)
				_board.Position = Vector2.Zero;
		}

		if (_phase == Phase.Play)
		{
			_acc += (float)delta;
			// 用 while：卡顿一下攒了两行时间时要把该落的都落掉，否则累积越来越慢
			while (_acc >= _gravity)
			{
				_acc -= _gravity;
				MoveDown();
				if (_phase != Phase.Play)
					break;
			}
			anim = true;
		}

		if (anim)
			Redraw();
	}

	// ===================== 操作 =====================

	private void DoMoveLeft()
	{
		if (_phase == Phase.Play && Move(-1, 0))
			Redraw();
	}

	private void DoMoveRight()
	{
		if (_phase == Phase.Play && Move(1, 0))
			Redraw();
	}

	private void DoSoftDrop()
	{
		if (_phase != Phase.Play)
			return;
		_acc = 0f; // 手动下坠之后重新计时，不然连按会把重力那一刻也吃掉
		if (MoveDown())
			_score += 1;
		UpdateHud();
		Redraw();
	}

	private void DoRotate()
	{
		if (_phase == Phase.Play && Rotate())
			Redraw();
	}

	private void DoHardDrop()
	{
		if (_phase != Phase.Play)
			return;
		_acc = 0f;
		HardDrop();
		Redraw();
	}

	private void TogglePause()
	{
		if (_phase == Phase.Play)
		{
			_phase = Phase.Paused;
			ShowOverlay("已暂停", "← → 移动 · ↑ 旋转 · ↓ 加速 · 空格直落", "", true);
		}
		else if (_phase == Phase.Paused)
		{
			Resume();
		}
	}

	private void Resume()
	{
		if (_phase != Phase.Paused)
			return;
		_phase = Phase.Play;
		_acc = 0f;
		_overlay.Visible = false;
	}

	/// <summary>
	/// 键盘走 _Input。棋盘不是 Control，用 _UnhandledInput 会被铺满全屏的根 Control 截掉。
	/// 同时要过滤「模拟事件」（device = -1）：项目开了 mouse→touch，一次按键会收到两遍。
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		if (@event is not InputEventKey k || !k.Pressed || k.Echo || k.Device == -1)
			return;

		switch (k.Keycode)
		{
			case Key.Left: DoMoveLeft(); break;
			case Key.Right: DoMoveRight(); break;
			case Key.Down: DoSoftDrop(); break;
			case Key.Up: DoRotate(); break;
			case Key.Space: DoHardDrop(); break;
			case Key.P: TogglePause(); break;
			default:
				switch (k.PhysicalKeycode)
				{
					case Key.A: DoMoveLeft(); break;
					case Key.D: DoMoveRight(); break;
					case Key.S: DoSoftDrop(); break;
					case Key.W: DoRotate(); break;
				}
				break;
		}
	}

	// ===================== 绘制 =====================

	private void Redraw() => _gridView.QueueRedraw();

	private Rect2 CellRect(int c, int r)
		=> new(c * _tile, r * _tile, _tile, _tile);

	private void DrawBoard(CanvasItem ci)
	{
		float w = _tile * Cols, h = _tile * Rows;

		ci.DrawStyleBox(_sbFrame, new Rect2(-16f, -16f, w + 32f, h + 32f));
		ci.DrawStyleBox(_sbFelt, new Rect2(0f, 0f, w, h));

		// 井格线
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				ci.DrawStyleBox(_sbCellLine, new Rect2(c * _tile + 1f, r * _tile + 1f, _tile - 2f, _tile - 2f));

		// 已经固定在井里的方块
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				if (_grid[r, c] != 0)
					DrawBlock(ci, CellRect(c, r), Pieces[_grid[r, c] - 1].Tint, 1f);

		// 落点预览（幽灵块）：贴着地，用很淡的颜色，方便判断落哪
		if (_phase == Phase.Play)
		{
			int drop = 0;
			while (!Collides(_piece, new Vector2I(_piecePos.X, _piecePos.Y + drop + 1)))
				drop++;
			if (drop > 0)
			{
				int n = _piece.GetLength(0);
				for (int r = 0; r < n; r++)
					for (int c = 0; c < n; c++)
						if (_piece[r, c] != 0)
							DrawBlock(ci, CellRect(_piecePos.X + c, _piecePos.Y + r + drop),
								Pieces[_pieceColor - 1].Tint, 0.20f);
			}
		}

		// 当前方块
		{
			int n = _piece.GetLength(0);
			for (int r = 0; r < n; r++)
				for (int c = 0; c < n; c++)
					if (_piece[r, c] != 0 && _piecePos.Y + r >= 0)
						DrawBlock(ci, CellRect(_piecePos.X + c, _piecePos.Y + r),
							Pieces[_pieceColor - 1].Tint, 1f);
		}

		// 消行的白闪
		foreach (var p in _pops)
		{
			float t = Mathf.Clamp(p.Age / PopLife, 0f, 1f);
			_sbFlash.BgColor = new Color(1f, 1f, 1f, 0.85f * (1f - t));
			ci.DrawStyleBox(_sbFlash, new Rect2(0f, p.Row * _tile, w, _tile));
		}

		DrawSidePanel(ci, w, h);
	}

	/// <summary>井两侧的辅助信息：左边最高分，右边「下一个」预览。</summary>
	private void DrawSidePanel(CanvasItem ci, float boardW, float boardH)
	{
		float leftW = _origin.X - 16f;
		if (leftW > 60f)
		{
			DrawText(ci, new Rect2(-leftW, 0f, leftW, 36f), "最高", new Color(1f, 1f, 1f, 0.55f), 26, HorizontalAlignment.Left);
			DrawText(ci, new Rect2(-leftW, 36f, leftW, 46f), _best.ToString(), new Color("#ffd77a"), 38, HorizontalAlignment.Left);
		}

		float rightX = boardW + 14f;
		float rightW = StageW - (_origin.X + boardW) - 26f;
		if (rightW < 80f)
			return;

		ci.DrawStyleBox(_sbPanel, new Rect2(rightX, 0f, rightW, 178f));
		DrawText(ci, new Rect2(rightX, 8f, rightW, 32f), "下一个", new Color(1f, 1f, 1f, 0.60f), 24, HorizontalAlignment.Center);

		// 把下一块按格子画在面板里（先量出它的包围盒再居中）
		var def = Pieces[_nextIndex];
		int n = def.Cells.GetLength(0);
		int minC = n, maxC = -1, minR = n, maxR = -1;
		for (int r = 0; r < n; r++)
			for (int c = 0; c < n; c++)
				if (def.Cells[r, c] != 0)
				{
					if (c < minC) minC = c;
					if (c > maxC) maxC = c;
					if (r < minR) minR = r;
					if (r > maxR) maxR = r;
				}
		if (maxC < 0)
			return;

		float cell = Mathf.Min(rightW / (maxC - minC + 1), 96f / (maxR - minR + 1));
		cell = Mathf.Min(cell, 26f);
		float gw = (maxC - minC + 1) * cell;
		float gh = (maxR - minR + 1) * cell;
		var box = new Rect2(rightX + (rightW - gw) * 0.5f, 62f + (96f - gh) * 0.5f, gw, gh);

		for (int r = minR; r <= maxR; r++)
			for (int c = minC; c <= maxC; c++)
				if (def.Cells[r, c] != 0)
					DrawBlock(ci, new Rect2(
						box.Position.X + (c - minC) * cell,
						box.Position.Y + (r - minR) * cell,
						cell, cell), def.Tint, 1f);
	}

	private void DrawBlock(CanvasItem ci, Rect2 rect, Color tint, float alpha)
	{
		_sbBlockEdge.BgColor = new Color(0.02f, 0.03f, 0.06f, alpha);
		ci.DrawStyleBox(_sbBlockEdge, rect);

		_sbBlock.BgColor = new Color(tint.R, tint.G, tint.B, alpha);
		var inner = new Rect2(rect.Position + new Vector2(2f, 2f), rect.Size - new Vector2(4f, 4f));
		ci.DrawStyleBox(_sbBlock, inner);

		// 左上角一道高光，让方块看起来是立体的
		ci.DrawRect(new Rect2(
			inner.Position + new Vector2(inner.Size.X * 0.16f, inner.Size.Y * 0.16f),
			new Vector2(inner.Size.X * 0.68f, inner.Size.Y * 0.15f)), new Color(1f, 1f, 1f, 0.38f * alpha));
	}

	/// <summary>在框里画一行字（垂直居中要自己按 ascent/descent 算基线）。</summary>
	private void DrawText(CanvasItem ci, Rect2 box, string text, Color color, int size,
		HorizontalAlignment align)
	{
		if (_font == null)
			return;
		size = Mathf.Max(10, size);
		float ascent = _font.GetAscent(size);
		float descent = _font.GetDescent(size);
		float baseline = box.GetCenter().Y + (ascent - descent) * 0.5f;
		ci.DrawString(_font, new Vector2(box.Position.X, baseline), text, align, box.Size.X, size, color);
	}

	// ===================== HUD / 面板 =====================

	private void BuildTopBar()
	{
		_topBar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_topBar.OffsetLeft = 20;
		_topBar.OffsetTop = 20;
		_topBar.OffsetRight = -20;
		_topBar.OffsetBottom = 100;
		_topBar.AddThemeConstantOverride("separation", 14);

		StyleBarButton(_homeButton, "返回", new Color("#8f7bff"));
		_homeButton.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);

		StyleBarButton(_restartButton, "重开", new Color("#ff8f6b"));
		_restartButton.Pressed += StartGame;

		StyleBarButton(_pauseButton, "暂停", new Color("#4fa8ff"));
		_pauseButton.Pressed += TogglePause;
	}

	private static void StyleBarButton(Button b, string text, Color bg)
	{
		GameArt.StyleButton(b, bg, Colors.White, fontSize: 32);
		b.CustomMinimumSize = new Vector2(140, 80);
		b.Text = text;
		// 方向键是键盘操作的核心，按钮抢走焦点后空格/方向键会被 GUI 吃掉，所以一律不要焦点
		b.FocusMode = FocusModeEnum.None;
	}

	private void BuildHud()
	{
		_scoreLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
		GameArt.OutlineText(_scoreLabel, new Color("#ffd77a"), 38, 8);
		_scoreLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_scoreLabel.GrowHorizontal = GrowDirection.Both;
		_scoreLabel.OffsetTop = 108;
		_scoreLabel.OffsetBottom = 164;
		_ui.AddChild(_scoreLabel);

		_hintLabel = new Label
		{
			Text = "← → 移动 · ↑ 旋转 · ↓ 加速 · 空格直落",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		_hintLabel.AddThemeFontSizeOverride("font_size", 24);
		_hintLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.55f));
		_hintLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		_hintLabel.GrowHorizontal = GrowDirection.Both;
		_hintLabel.GrowVertical = GrowDirection.Begin;
		_hintLabel.OffsetTop = -108;
		_hintLabel.OffsetBottom = -58;
		_ui.AddChild(_hintLabel);

		UpdateHud();
	}

	private void UpdateHud()
	{
		if (_score == _hudScore && _level == _hudLevel && _lines == _hudLines)
			return;
		_hudScore = _score;
		_hudLevel = _level;
		_hudLines = _lines;
		_scoreLabel.Text = $"得分 {_score} · 等级 {_level} · 消行 {_lines}";
	}

	private void BuildPad()
	{
		_btnLeft = MakePadButton("◀", new Color(1f, 1f, 1f, 0.14f), 40);
		_btnRight = MakePadButton("▶", new Color(1f, 1f, 1f, 0.14f), 40);
		_btnDown = MakePadButton("▼", new Color(1f, 1f, 1f, 0.14f), 40);
		_btnRotate = MakePadButton("旋转", new Color("#4fa8ff"), 30);
		_btnDrop = MakePadButton("直落到底", new Color("#ff8f6b"), 30);

		_btnLeft.Pressed += DoMoveLeft;
		_btnRight.Pressed += DoMoveRight;
		_btnDown.Pressed += DoSoftDrop;
		_btnRotate.Pressed += DoRotate;
		_btnDrop.Pressed += DoHardDrop;
	}

	private Button MakePadButton(string text, Color bg, int fontSize)
	{
		var b = new Button { Text = text, FocusMode = FocusModeEnum.None };
		GameArt.StyleButton(b, bg, Colors.White, radius: 22,
			border: new Color(1f, 1f, 1f, 0.34f), fontSize: fontSize);
		_ui.AddChild(b);
		return b;
	}

	private void LayoutPad()
	{
		PlaceButton(_btnLeft, new Vector2(PadLeft, PadTop), new Vector2(PadUnit, PadH));
		PlaceButton(_btnRight, new Vector2(PadLeft + PadUnit + 15f, PadTop), new Vector2(PadUnit, PadH));
		PlaceButton(_btnDown, new Vector2(PadLeft + (PadUnit + 15f) * 2f, PadTop), new Vector2(PadUnit, PadH));
		PlaceButton(_btnRotate, new Vector2(PadLeft + (PadUnit + 15f) * 3f, PadTop), new Vector2(PadUnit, PadH));
		PlaceButton(_btnDrop, new Vector2(PadLeft, DropTop), new Vector2(PadUnit * 4f + 45f, DropH));
	}

	/// <summary>按绝对坐标摆一个按钮（锚点钉在左上角，位置尺寸直接写像素）。</summary>
	private static void PlaceButton(Button b, Vector2 pos, Vector2 size)
	{
		b.AnchorLeft = 0f;
		b.AnchorTop = 0f;
		b.AnchorRight = 0f;
		b.AnchorBottom = 0f;
		b.OffsetLeft = pos.X;
		b.OffsetTop = pos.Y;
		b.OffsetRight = pos.X + size.X;
		b.OffsetBottom = pos.Y + size.Y;
		b.CustomMinimumSize = size;
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
		var box = GameArt.MakeBox(new Color(0.08f, 0.10f, 0.19f, 0.98f), 32, new Color(1f, 1f, 1f, 0.20f));
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
		_ovInfo.AddThemeFontSizeOverride("font_size", 28);
		_ovInfo.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.86f));
		col.AddChild(_ovInfo);

		_ovLine = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_ovLine.AddThemeFontSizeOverride("font_size", 30);
		_ovLine.AddThemeColorOverride("font_color", new Color("#ffd77a"));
		col.AddChild(_ovLine);

		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		row.AddThemeConstantOverride("separation", 16);
		col.AddChild(row);

		_ovPrimary = MakeOverlayButton("再来一局", new Color("#ff8f6b"));
		_ovPrimary.Pressed += () =>
		{
			if (_ovIsPause)
				Resume();
			else
				StartGame();
		};
		row.AddChild(_ovPrimary);

		_ovSecondary = MakeOverlayButton("回首页", new Color("#8f7bff"));
		_ovSecondary.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);
		row.AddChild(_ovSecondary);
	}

	private Button MakeOverlayButton(string text, Color bg)
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

	private void ShowOverlay(string title, string info, string line, bool pause)
	{
		_ovTitle.Text = title;
		_ovInfo.Text = info;
		_ovLine.Text = line;
		_ovLine.Visible = line.Length > 0;
		_ovPrimary.Text = pause ? "继续" : "再来一局";
		_ovIsPause = pause;
		_overlay.Visible = true;
	}

	// ===================== 最高分存档 =====================

	private void LoadBest()
	{
		var cfg = new ConfigFile();
		if (cfg.Load(BestPath) != Error.Ok)
			return;
		_best = cfg.GetValue("best", "score", 0).AsInt32();
	}

	private void SaveBest()
	{
		var cfg = new ConfigFile();
		cfg.SetValue("best", "score", _best);
		var err = cfg.Save(BestPath);
		if (err != Error.Ok)
			GD.PushError($"[Tetris] save best failed: {err}");
	}

	// ===================== 自测 =====================
	//
	// 触发方式：项目根目录放 selftest.flag，内容写 tetris，然后启动游戏
	// （首页会立刻切到本场景，本场景看到内容是自己就跑这一套）。

	private async Task RunSelfTestAsync()
	{
		GD.Print("[SELFTEST] begin (tetris)");
		try
		{
			await Wait(0.4);

			int fails = 0;
			_seed = 20260926; // 固定随机，跑出来的方块序列每次一样，出问题好复现
			// 自测会刷出好成绩，真去玩的人再也刷不掉，所以先备份玩家原有的纪录，验完写回去。
			int keepBest = _best;

			// ① 七种方块都在，且每种基准形状都是 4 格
			bool sevenOk = Pieces.Length == 7;
			bool fourOk = true;
			var names = new List<string>();
			foreach (var d in Pieces)
			{
				names.Add(d.Name);
				int cnt = 0;
				int n = d.Cells.GetLength(0);
				for (int r = 0; r < n; r++)
					for (int c = 0; c < n; c++)
						if (d.Cells[r, c] != 0)
							cnt++;
				if (cnt != 4)
					fourOk = false;
			}
			GD.Print($"[SELFTEST] 方块种类={Pieces.Length} [{string.Join(" ", names)}] 每种 4 格={fourOk} -> {sevenOk && fourOk}");
			if (!sevenOk || !fourOk) fails++;

			// ② 旋转四次回到原形，且每个朝向都还是「连在一起的 4 格」
			//    （这条盯的是「矩阵转置写反」这类错：转反了会出现行空列满、甚至把方块拆散）
			bool cycleOk = true, shapeOk = true;
			foreach (var d in Pieces)
			{
				var m = Clone(d.Cells);
				for (int i = 0; i < 4; i++)
				{
					m = RotateCw(m);
					if (!IsConnected4(m))
						shapeOk = false;
				}
				int n = d.Cells.GetLength(0);
				for (int r = 0; r < n; r++)
					for (int c = 0; c < n; c++)
						if (m[r, c] != d.Cells[r, c])
							cycleOk = false;
			}
			GD.Print($"[SELFTEST] 旋转四圈回原形={cycleOk} 每个朝向都是连着的 4 格={shapeOk} -> {cycleOk && shapeOk}");
			if (!cycleOk || !shapeOk) fails++;

			// ③ 七袋：连抽 7 个必须正好七种各一个
			StartGame();
			_bag.Clear();
			var seen = new HashSet<int>();
			for (int i = 0; i < Pieces.Length; i++)
				seen.Add(NextFromBag());
			bool bagOk = seen.Count == Pieces.Length;
			GD.Print($"[SELFTEST] 七袋: 前 7 个抽到 {seen.Count} 种 -> {bagOk}");
			if (!bagOk) fails++;

			// ④ 出块：落在井内、不重叠、并且给下一块也备好了
			StartGame();
			bool spawnOk = !Collides(_piece, _piecePos) && _piecePos.Y >= 0 && _nextIndex >= 0 && _nextIndex < Pieces.Length;
			GD.Print($"[SELFTEST] 出块: 位置={_piecePos} 撞={Collides(_piece, _piecePos)} 下一块={Pieces[_nextIndex].Name} -> {spawnOk}");
			if (!spawnOk) fails++;

			// ⑤ 左右移动 + 撞墙停住
			StartGame();
			_piece = Clone(Pieces[1].Cells); // O：2×2，好算
			_pieceColor = 2;
			int x0 = _piecePos.X;
			Move(-1, 0);
			bool movedOk = _piecePos.X == x0 - 1;
			for (int i = 0; i < Cols + 4; i++)
				Move(-1, 0);
			bool wallOk = _piecePos.X == 0;
			GD.Print($"[SELFTEST] 左右移动: {x0}->{x0 - 1} 左移={movedOk} 顶到左墙 x={_piecePos.X} -> {wallOk}");
			if (!movedOk || !wallOk) fails++;

			// ⑥ 落到底会自动锁定：井里多出 4 格
			StartGame();
			_piece = Clone(Pieces[1].Cells);
			_pieceColor = 2;
			_piecePos = new Vector2I(0, 0);
			int placed = 0;
			while (MoveDown())
				placed++;
			int onGrid = CountGrid();
			bool lockOk = onGrid == 4 && placed > 0;
			GD.Print($"[SELFTEST] 落地锁定: 下落 {placed} 行 井里 {onGrid} 格 -> {lockOk}");
			if (!lockOk) fails++;

			// ⑦ 消 1 行：铺满最底行只留第 0 列，再把一个单格方块落进去
			StartGame();
			ClearGrid();
			for (int c = 1; c < Cols; c++)
				_grid[Rows - 1, c] = 1;
			_piece = new int[1, 1] { { 1 } };
			_pieceColor = 1;
			_piecePos = new Vector2I(0, 0);
			while (MoveDown())
			{
			}
			bool oneLineOk = _lines == 1 && _score == 100 && CountGrid() == 0;
			GD.Print($"[SELFTEST] 消 1 行: 行数={_lines} 得分={_score} 井里剩余={CountGrid()} -> {oneLineOk}");
			if (!oneLineOk) fails++;

			// ⑧ 一次消 4 行：铺满最底 4 行只留第 0 列，再塞一根竖着的 4 格长条
			StartGame();
			ClearGrid();
			for (int r = Rows - 4; r < Rows; r++)
				for (int c = 1; c < Cols; c++)
					_grid[r, c] = 1;
			// 注意方块一律是**方阵**（4×4 / 3×3 / 2×2，见 Pieces）：Collides / Lock / DrawBoard
			// 都是按 n×n 遍历的，塞一个 4×1 进去当场越界。
			var bar = new int[4, 4];
			for (int r = 0; r < 4; r++)
				bar[r, 0] = 1;
			_piece = bar;
			_pieceColor = 1;
			_piecePos = new Vector2I(0, 0);
			while (MoveDown())
			{
			}
			bool fourLineOk = _lines == 4 && _score == 1000 && CountGrid() == 0 && _pops.Count == 4;
			GD.Print($"[SELFTEST] 一次消 4 行: 行数={_lines} 得分={_score} 井里剩余={CountGrid()} 白闪={_pops.Count} -> {fourLineOk}");
			if (!fourLineOk) fails++;

			// ⑨ 升级提速：每 10 行一级，重力必须变快（但不会快过下限）
			StartGame();
			_gravity = BaseGravity;
			_lines = 0;
			float g1 = _gravity;
			ClearGrid();
			for (int r = Rows - 1; r >= Rows - 10; r--)
				for (int c = 1; c < Cols; c++)
					_grid[r, c] = 1;
			// 一列一列地把整行补满：10 行 = 正好升一级
			for (int r = Rows - 10; r < Rows; r++)
				_grid[r, 0] = 1;
			int got = ClearLines();
			bool levelOk = got == 10 && _level == 2 && _gravity < g1 && _gravity >= MinGravity;
			GD.Print($"[SELFTEST] 升级提速: 消 {got} 行 等级={_level} 重力 {g1:0.###}->{_gravity:0.###}s -> {levelOk}");
			if (!levelOk) fails++;

			// ⑩ 井口被堵住 → 结束
			StartGame();
			ClearGrid();
			for (int r = 0; r < 2; r++)
				for (int c = 0; c < Cols; c++)
					_grid[r, c] = 1;
			_nextIndex = 2; // T：正文占 matrix 的第 0、1 行，正好和堵住的两行重叠
			_pops.Clear();
			_phase = Phase.Play;
			_overlay.Visible = false;
			SpawnPiece();
			bool topOk = _phase == Phase.Over && _overlay.Visible;
			GD.Print($"[SELFTEST] 井口堵住: phase={_phase} overlay={_overlay.Visible} -> {topOk}");
			if (!topOk) fails++;

			// ⑪ 暂停：暂停后重力不再下落
			StartGame();
			_gravity = 0.05f;
			TogglePause();
			bool pausedOk = _phase == Phase.Paused && _overlay.Visible && _ovPrimary.Text == "继续";
			int yP = _piecePos.Y;
			await Wait(0.4);
			bool frozenOk = _piecePos.Y == yP;
			Resume();
			bool resumedOk = _phase == Phase.Play && !_overlay.Visible;
			GD.Print($"[SELFTEST] 暂停/继续: 暂停={pausedOk} 0.4s 后 y 不动={frozenOk} 恢复={resumedOk}");
			if (!pausedOk || !frozenOk || !resumedOk) fails++;

			// ⑫ 最高分真的落盘了
			StartGame();
			_best = 0;
			_score = 4321;
			GameOver();
			var cfg = new ConfigFile();
			int saved = cfg.Load(BestPath) == Error.Ok ? cfg.GetValue("best", "score", -1).AsInt32() : -1;
			bool bestOk = _best == 4321 && saved == 4321;
			GD.Print($"[SELFTEST] 最高分落盘: mem={_best} file={saved} -> {bestOk}");
			if (!bestOk) fails++;
			SaveShot("tetris_over");

			// 还原玩家的纪录（含文件）
			_best = keepBest;
			SaveBest();

			// ⑬ 真实 GUI 链路：点屏幕上的「◀」，方块必须真的左移一格
			StartGame();
			_piece = Clone(Pieces[1].Cells);
			_pieceColor = 2;
			_piecePos = new Vector2I(4, 0);
			PushGuiClick(_btnLeft.GetGlobalRect().GetCenter());
			await Wait(0.15);
			bool guiOk = _piecePos.X == 3;
			GD.Print($"[SELFTEST] gui 点「◀」: x=4->{_piecePos.X} -> {guiOk}");
			if (!guiOk) fails++;

			// ⑭ 布局：井装得进可用区、格子不小于下限、按键在井下方且不越屏
			bool gridOk = _tile >= MinTile && _tile * Cols <= StageW - PadX * 2f + 0.5f &&
						  _origin.Y >= BoardTop - 0.5f && _origin.Y + _tile * Rows <= BoardBottom + 0.5f;
			float padRight = PadLeft + (PadUnit + 15f) * 4f - 15f;
			bool padOk = _btnLeft.OffsetTop >= _origin.Y + _tile * Rows && padRight <= StageW;
			GD.Print($"[SELFTEST] 布局: tile={_tile} origin={_origin} 井下沿={_origin.Y + _tile * Rows:0.#} " +
					 $"按键右沿={padRight:0.#} 屏宽={StageW:0.#} -> {gridOk && padOk}");
			if (!gridOk || !padOk) fails++;

			// ⑮ 背景真的铺满了吗（露出 0.3 灰的清屏色就说明背景没覆盖整屏）
			var shot = GetViewport().GetTexture().GetImage();
			var px = new Vector2I((int)(shot.GetWidth() * 0.01f), (int)(shot.GetHeight() * 0.5f));
			var col = shot.GetPixel(px.X, px.Y);
			bool bgOk = !GameArt.IsClearColor(col);
			GD.Print($"[SELFTEST] background covers screen: pixel{px}={col} -> {bgOk}");
			if (!bgOk) fails++;

			// ⑮b 方块真的画出来了吗：取当前方块第一格中心的像素，必须是它的颜色。
			//      光验数据/逻辑的断言盯不住「画布根本没重画」这类问题。
			StartGame();
			_piece = Clone(Pieces[1].Cells); // O：2×2，颜色 #ffd23f（黄色）
			_pieceColor = 2;
			_piecePos = new Vector2I(4, 2);
			_phase = Phase.Paused; // 冻住，别让重力把它带走
			Redraw();
			await Wait(0.2);
			var boardShot = GetViewport().GetTexture().GetImage();
			float kx = boardShot.GetWidth() / DesignW;
			float ky = boardShot.GetHeight() / DesignH;
			var cellC = _origin + new Vector2((4 + 0.5f) * _tile, (2 + 0.5f) * _tile);
			var cellPx = new Vector2I((int)(cellC.X * kx), (int)(cellC.Y * ky));
			var cellCol = boardShot.GetPixel(cellPx.X, cellPx.Y);
			bool blockVisible = cellCol.R > 0.55f && cellCol.G > 0.45f && cellCol.B < 0.45f;
			GD.Print($"[SELFTEST] 方块可见: pixel{cellPx}={cellCol} -> {blockVisible}");
			if (!blockVisible) fails++;

			// ⑯ 正常玩一会儿，截一张中局井面（先手工堆几行再叠一块）
			StartGame();
			ClearGrid();
			int[] heights = { 3, 5, 2, 6, 4, 3, 5, 2, 4, 3 };
			for (int c = 0; c < Cols; c++)
				for (int r = Rows - heights[c]; r < Rows; r++)
					_grid[r, c] = (c % 7) + 1;
			_piece = Clone(Pieces[2].Cells); // T
			_pieceColor = 3;
			_piecePos = new Vector2I(3, 4);
			_phase = Phase.Play;
			await Wait(0.3);
			SaveShot("tetris_board");

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

	private int CountGrid()
	{
		int n = 0;
		for (int r = 0; r < Rows; r++)
			for (int c = 0; c < Cols; c++)
				if (_grid[r, c] != 0)
					n++;
		return n;
	}

	/// <summary>
	/// 这个形状是不是「连在一起的 4 格」。四种朝向都该满足：
	/// 旋转写错会把方块拆成两块（比如 010/010/010 那种一条线少一格），长得不像任何一种俄罗斯方块。
	/// </summary>
	private static bool IsConnected4(int[,] m)
	{
		int n = m.GetLength(0);
		int startR = -1, startC = -1, total = 0;
		for (int r = 0; r < n; r++)
			for (int c = 0; c < n; c++)
				if (m[r, c] != 0)
				{
					total++;
					if (startR < 0)
					{
						startR = r;
						startC = c;
					}
				}
		if (total != 4)
			return false;

		// 从第一格开始四邻域洪泛，能把 4 格都走到才算连在一起
		var seen = new bool[n, n];
		var stack = new Stack<Vector2I>();
		stack.Push(new Vector2I(startC, startR));
		seen[startR, startC] = true;
		int reached = 1;
		var steps = new[] { new Vector2I(1, 0), new Vector2I(-1, 0), new Vector2I(0, 1), new Vector2I(0, -1) };
		while (stack.Count > 0)
		{
			var p = stack.Pop();
			foreach (var d in steps)
			{
				int c = p.X + d.X, r = p.Y + d.Y;
				if (c < 0 || r < 0 || c >= n || r >= n || seen[r, c] || m[r, c] == 0)
					continue;
				seen[r, c] = true;
				reached++;
				stack.Push(new Vector2I(c, r));
			}
		}
		return reached == 4;
	}

	/// <summary>把一次点击推给视口，走引擎的 GUI 命中测试（能测出「事件根本没送到按钮」）。</summary>
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
		GD.Print($"[Tetris] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
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

	/// <summary>井的画布。逻辑都在 TetrisGame 里，它只负责把 _Draw 转发过去。</summary>
	private sealed partial class GridView : Node2D
	{
		private readonly TetrisGame _game;

		public GridView(TetrisGame game)
		{
			_game = game;
		}

		public override void _Draw() => _game.DrawBoard(this);
	}
}
