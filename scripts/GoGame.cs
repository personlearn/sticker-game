#nullable enable
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 围棋 —— 人机对战（首页卡片里的「棋类 · 围地」）。
///
/// <para>
/// 三档只有「棋盘路数 / 贴目」两个数字不一样（见 <see cref="Levels"/>），
/// 规则和电脑完全共用一套，代码里没有一处按难度分叉。
/// 你执黑先下，电脑执白。
/// </para>
///
/// <para>
/// 电脑是**启发式规则机器人**，不做蒙特卡洛树搜索 —— 每个空点算一个分数，
/// 取最高的那个。评分项依次是：提子、救自己被打吃的棋、打吃对方、气的多少、
/// 别自填眼、别填自己的空、三四线的位置感、跟着上一手走、以及一点点随机。
/// 这套东西下不过认真学过棋的人，但对付刚开始玩的人已经会抢角、会吃子、会救棋，
/// 也不会傻乎乎地往自己眼里填子。具体权重见 <see cref="ChooseAiMove"/>。
/// </para>
///
/// <para>
/// 结构沿用全项目的约定：一个脚本装下全部逻辑与绘制，场景里只有
/// Stage / Background / Board / UI / TopBar 这个骨架，其余节点全部代码创建，
/// 所有图形都是 _Draw 现画的（不依赖任何素材文件）。
/// </para>
///
/// <para>
/// 围棋和前面几个游戏最大的不同是「终局」：不是把格子填满，而是双方连续停一手之后
/// 数子。数子前给一个**标死子**的阶段 —— 死子算谁的空，程序猜不准，交给玩家点一下最稳。
/// </para>
///
/// <para>
/// 除了三档自由对局，还有一套<b>残局挑战</b>（见 <see cref="GoPuzzles"/>）：
/// 从摆好的局部局面开始，带着一个明确目标继续和电脑下。达成目标给过关反馈，
/// 但棋局不中断 —— 也可以一路下到双方停一手数子，用「对战」把残局下完。
/// 本场景实现 <see cref="IResumable"/>，切后台被系统杀掉后能从原局面接回来。
/// </para>
/// </summary>
public partial class GoGame : Control, IResumable
{
	// ===================== 可调参数 =====================

	private const float PadX = 20f;          // 棋盘左右留白
	private const float BoardTop = 214f;     // 棋盘可用区上边界（720×1280 逻辑分辨率下的绝对值）
	private const float BoardBottom = 1104f; // 棋盘可用区下边界（下面留给「停一手 / 悔棋」）
	private const float MinCell = 24f;       // 格距下限：再小就点不准交叉点了
	private const float PanelPad = 0.62f;    // 棋盘木边宽度 = 格距 × 这个系数
	private const float AiDelay = 0.42f;     // 电脑「思考」的停顿（纯粹为了手感，不是真在算）

	private const float PopLife = 0.24f;     // 落子那一下的光环时长
	private const float RingLife = 0.50f;    // 提子光环的时长

	private const sbyte Empty = 0;
	private const sbyte Black = 1;
	private const sbyte White = 2;

	private const string SfxPath = "res://sfx/ding.wav";
	private const int SfxVoices = 4;

	private static readonly int[] Dx = { 1, -1, 0, 0 };
	private static readonly int[] Dy = { 0, 0, 1, -1 };

	// ===================== 难度表 =====================

	private sealed class LevelDef
	{
		public string Name = "";   // 面板上显示的名字
		public string Tag = "";    // 存档 / 日志里的 key
		public int Size;           // 路数（9 / 13 / 19）
		public float Komi;         // 贴目（白棋先欠这么多，数子时直接加给白）
		public Color Tint = Colors.White;
	}

	/// <summary>
	/// 三档难度。贴目按中国规则「数子法」给：19 路 7.5 目是标准，
	/// 小棋盘按比例收一点（9 路还给 7.5 的话黑棋要赢太多子才追得上）。
	/// </summary>
	private static readonly LevelDef[] Levels =
	{
		new LevelDef { Name = "入门", Tag = "9", Size = 9, Komi = 5.5f, Tint = new Color("#7ee787") },
		new LevelDef { Name = "初级", Tag = "13", Size = 13, Komi = 6.5f, Tint = new Color("#8fd3ff") },
		new LevelDef { Name = "进阶", Tag = "19", Size = 19, Komi = 7.5f, Tint = new Color("#ffb46b") },
	};

	// ===================== 棋盘数据 =====================

	private enum Phase { Setup, Play, Scoring, Done }

	private sbyte[] _board = Array.Empty<sbyte>();
	private bool[] _dead = Array.Empty<bool>();
	private sbyte[] _owner = Array.Empty<sbyte>(); // 数子时每个空点归谁：0 无主 / 1 黑 / 2 白
	private int _n = 9;
	private int _levelIndex;

	private int _turn = Black;    // 该谁下
	private int _passes;          // 连续停一手次数
	private int _capBlack;        // 黑棋提掉的白子数
	private int _capWhite;        // 白棋提掉的黑子数
	private int _moveCount;
	private int _lastMove = -1;   // 最后一手的位置（-1 = 没有 / 停了一手）
	private Phase _phase = Phase.Setup;

	private float _blackScore, _whiteScore; // 数子结果（只在 Scoring / Done 有效）

	/// <summary>
	/// 打劫禁着：上一步**之前**的局面哈希。新局面的哈希等于它，就是「提回来」，
	/// 这一手不许下（简单劫，够用了；不搞全局同形）。
	/// </summary>
	private ulong _prevHash;

	private ulong[] _zob = Array.Empty<ulong>();

	// 规则用的临时缓冲：反复 new 会很脏，AI 一次要模拟几百手
	private int[] _stamp = Array.Empty<int>();
	private int[] _libStamp = Array.Empty<int>();
	private int _stampId;
	private readonly Stack<int> _flood = new();
	private readonly List<int> _gbuf = new();
	private readonly int[] _distBlack = new int[19 * 19];
	private readonly int[] _distWhite = new int[19 * 19];
	private readonly Queue<int> _bfs = new();

	// ===================== 悔棋 =====================

	private sealed class Snap
	{
		public sbyte[] Board = Array.Empty<sbyte>();
		public ulong PrevHash;
		public int Turn, Passes, CapBlack, CapWhite, LastMove, MoveCount;
	}

	private readonly List<Snap> _undo = new();

	// ===================== 动画 =====================

	private sealed class Pop { public Vector2 Pos; public float Age; public Color Tint = Colors.White; }
	private sealed class Ring { public Vector2 Pos; public float Age; public Color Tint = Colors.White; }

	private readonly List<Pop> _pops = new();
	private readonly List<Ring> _rings = new();

	// ===================== 输入 / AI 状态 =====================

	private int _hover = -1;
	private float _aiWait;
	private int _aiDelayMs;
	private int _seed;              // 自测里固定随机数，盘面可复现
	private Random _aiRng = new();
	private bool _aiThinking;       // 这一帧是不是刚起手（只用于 HUD 文案）

	// ===================== 子节点 =====================

	private Control _stage = null!;
	private TextureRect _background = null!;
	private Node2D _boardNode = null!;
	private Control _ui = null!;
	private HBoxContainer _topBar = null!;
	private Button _homeButton = null!;
	private Button _setupButton = null!;
	private Button _restartButton = null!;

	private BoardView _view = null!;

	private HBoxContainer _hud = null!;
	private Label _blackLabel = null!;
	private Label _turnLabel = null!;
	private Label _whiteLabel = null!;
	private Label _levelLabel = null!;

	private HBoxContainer _bottom = null!;
	private Button _undoButton = null!;
	private Button _passButton = null!;
	private Button _resumeButton = null!;
	private Button _settleButton = null!;
	private Label _hintLabel = null!;
	private Label _toastLabel = null!;
	private Tween? _toastTween;

	private Control _setup = null!;
	private readonly Button[] _levelButtons = new Button[Levels.Length];
	private readonly Label[] _levelSubs = new Label[Levels.Length];
	private Button _resumeGameButton = null!;
	private bool _resumable;
	private Phase _resumeTo = Phase.Play;

	// ===================== 残局挑战 =====================
	// 题库在 scripts/GoPuzzles.cs。_puzzleAiBan 是「电脑不许下」的局部战场，
	// 由 BuildPuzzleZone() 按初始棋子 + 答案点外扩一格算出来。

	private PuzzleDef? _puzzle;
	private int _puzzleIndex = -1;
	private int[] _puzzleKeys = Array.Empty<int>();
	private bool[] _puzzleAiBan = Array.Empty<bool>();
	private int _puzzleZoneX0, _puzzleZoneY0, _puzzleZoneX1, _puzzleZoneY1;
	private int _puzzleMoves;
	private bool _puzzleSolved;
	private readonly Button[] _puzzleButtons = new Button[GoPuzzles.All.Length];

	private ColorRect _result = null!;
	private Label _resultTitle = null!;
	private Label _resultInfo = null!;
	private Label _resultSub = null!;
	private Button _resultAgain = null!;
	private Button _resultChange = null!;
	private Button _resultQuit = null!;

	// ===================== 联机 =====================
	//
	// 三个状态要说清楚，不然后面全是 if 套 if：
	//   _lan == null      —— 完全没在联机（人机模式，或根本没进过联机面板）
	//   _lan != null      —— 面板开着 / 房间挂着（可能在等对手、可能在连接）
	//   _lanLive          —— 真的在对局中：电脑不动、点击走网络、HUD 写对手的名字
	// 面板打开时 _lan 有值但 _lanLive 还是 false，棋盘被 setup 面板挡着，不会误触。

	private LanNet? _lan;
	private bool _lanLive;
	private sbyte _myColor = Black;
	private string _myName = "";

	private Control _lanPanel = null!;
	private Label _lanHead = null!;
	private VBoxContainer _lanBody = null!;
	private LineEdit? _lanIpInput;
	private string _lanIpText = "";
	private string _lanSig = "";
	private float _lanUiCd;

	/// <summary>联机模式（对局中）。人机逻辑一律看这个，不看 <see cref="_lan"/>。</summary>
	private bool IsLan => _lanLive;

	// 画棋盘用的样式盒（缓存起来，_Draw 里不能每次 new）
	private StyleBoxFlat _sbWood = null!;
	private StyleBoxFlat _sbPanel = null!;

	private float _cell = 40f;
	private Vector2 _origin;
	private (int X, int Y)[] _stars = Array.Empty<(int X, int Y)>();

	private readonly List<AudioStreamPlayer> _sfxPool = new();
	private int _sfxIndex;
	private bool _hasSfx;

	private float StageW => Size.X > 0f ? Size.X : 720f;
	private float StageH => Size.Y > 0f ? Size.Y : 1280f;

	// ===================== 生命周期 =====================

	public override void _Ready()
	{
		_stage = GetNode<Control>("Stage");
		_background = GetNode<TextureRect>("Stage/Background");
		_boardNode = GetNode<Node2D>("Stage/Board");
		_ui = GetNode<Control>("UI");
		_topBar = GetNode<HBoxContainer>("UI/TopBar");
		_homeButton = GetNode<Button>("UI/TopBar/HomeButton");
		_setupButton = GetNode<Button>("UI/TopBar/SetupButton");
		_restartButton = GetNode<Button>("UI/TopBar/RestartButton");

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		// 根节点铺满全屏，默认 MouseFilter = Stop 会把空白处的点击全「认领」掉，
		// 棋盘画在 Node2D 上、靠代码自己接事件，被认领就等于点不动。
		MouseFilter = MouseFilterEnum.Ignore;
		Theme = GameArt.MakeUiTheme();

		// 木色背景：深一点的暖棕，和棋盘的浅木色拉开层次
		_background.Texture = GameArt.VerticalGradient(new Color("#1b1610"), new Color("#33291c"));
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
		_background.MouseFilter = MouseFilterEnum.Ignore;

		// 棋盘自己画自己：所有线、子、标记都在这一个节点的 _Draw 里出来。
		// ZIndex 保持 0 —— UI 是 Stage 的兄弟节点，给正数就会盖住顶栏和结算遮罩。
		_view = new BoardView(this);
		_boardNode.AddChild(_view);

		BuildStyleBoxes();
		BuildSfxPool();
		BuildTopBar();
		BuildHud();
		BuildBottom();
		BuildSetup();
		BuildLan();
		BuildResult();

		Layout();
		GetViewport().SizeChanged += Layout;
		Callable.From(Layout).CallDeferred(); // 首帧尺寸才稳定

		// 先开一局：自测是 fire-and-forget 协程，_Process 会立刻开始跑，
		// 不能让它拿着半初始化的状态（见项目笔记里的踩坑记录）。
		StartGame(0);

		GD.Print($"[Go] ready. selftest={SelftestFlag.Describe()}");

		if (SelftestFlag.Read() == SelftestFlag.TokenGo)
			_ = RunSelfTestAsync();
		else if (ResumeState.TryRestore(this, out var saved))
			Restore(saved);
		else
			ShowSetup();
	}

	/// <summary>离开这个场景（回首页）时把占着的端口和连接放掉。</summary>
	public override void _ExitTree()
	{
		_lan?.Dispose();
		_lan = null;
		_lanLive = false;
	}

	private void Layout()
	{
		// Control 不会自动铺满父节点：尺寸是 0 的话里面全塌缩成一列看不见的东西
		_stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_stage.MouseFilter = MouseFilterEnum.Ignore;
		_ui.MouseFilter = MouseFilterEnum.Ignore;

		// 背景必须显式铺满 + 关掉「最小尺寸 = 纹理尺寸」，否则只在左上角画一小块
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_background.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;

		ComputeGrid();
	}

	/// <summary>
	/// 按路数算格距和棋盘原点。木边也算进可用区，这样 19 路也能把整块棋盘
	/// 摆在 360px 左右的可视宽度里 —— 交叉点仍然有 35px 的间距，手指够得着。
	/// </summary>
	private void ComputeGrid()
	{
		if (_n < 2)
			return;

		float availW = StageW - PadX * 2f;
		float availH = BoardBottom - BoardTop;
		float span = _n - 1 + PanelPad * 2f;
		float cell = Mathf.Min(availW / span, availH / span);
		cell = Mathf.Max(MinCell, Mathf.Floor(cell * 10f) / 10f);
		_cell = cell;

		float gw = cell * (_n - 1);
		_origin = new Vector2(
			(StageW - gw) * 0.5f,
			BoardTop + (availH - gw) * 0.5f);
	}

	private void BuildStyleBoxes()
	{
		_sbWood = GameArt.MakeBox(new Color("#e5bd86"), 16, new Color("#7d4f24"));
		_sbWood.ContentMarginLeft = _sbWood.ContentMarginRight = 0;
		_sbWood.ContentMarginTop = _sbWood.ContentMarginBottom = 0;
		_sbPanel = GameArt.MakeBox(new Color(0.06f, 0.05f, 0.04f, 0.62f), 22, new Color(1f, 1f, 1f, 0.10f));
	}

	// ===================== 一局的开始与结束 =====================

	private void StartGame(int levelIndex)
	{
		_levelIndex = Mathf.Clamp(levelIndex, 0, Levels.Length - 1);
		var def = Levels[_levelIndex];

		ClearPuzzle();   // 普通对局不带残局状态；StartPuzzle 会在之后重新装上

		_n = def.Size;
		int total = _n * _n;
		_board = new sbyte[total];
		_dead = new bool[total];
		_owner = new sbyte[total];
		_stamp = new int[total];
		_libStamp = new int[total];
		_zob = new ulong[total * 2];
		BuildZobrist();

		_stars = StarPoints(_n);
		_undo.Clear();
		_pops.Clear();
		_rings.Clear();

		_turn = Black;
		_passes = 0;
		_capBlack = 0;
		_capWhite = 0;
		_moveCount = 0;
		_lastMove = -1;
		_prevHash = 0;
		_stampId = 0;
		_blackScore = 0f;
		_whiteScore = 0f;
		_hover = -1;
		_aiWait = 0f;
		_aiThinking = false;
		_aiRng = new Random(_seed != 0 ? _seed : (int)(Time.GetTicksMsec() & 0x7FFFFFFF));

		_phase = Phase.Play;
		_setup.Visible = false;
		_result.Visible = false;
		_resumeTo = Phase.Play;

		ComputeGrid();
		UpdateScoringUi();
		UpdateHud();
		Redraw();

		GD.Print($"[Go] start \"{def.Name}\" {_n} 路 · 贴 {def.Komi:0.0} 目");
	}

	// ===================== 残局挑战 =====================

	/// <summary>清掉残局状态。开普通对局时由 StartGame 调用。</summary>
	private void ClearPuzzle()
	{
		_puzzle = null;
		_puzzleIndex = -1;
		_puzzleKeys = Array.Empty<int>();
		_puzzleAiBan = Array.Empty<bool>();
		_puzzleMoves = 0;
		_puzzleSolved = false;
	}

	/// <summary>
	/// 开一道残局：先走 StartGame 把整局状态复位，再把题目摆上盘。
	/// 这是「开残局」的唯一入口，续玩恢复也走它。
	/// </summary>
	private void StartPuzzle(int index)
	{
		index = Mathf.Clamp(index, 0, GoPuzzles.All.Length - 1);
		var def = GoPuzzles.All[index];

		StartGame(GoPuzzles.LevelFor(def.Size));   // 里面会 ClearPuzzle

		// 开新题时把上一题的过关 Toast 收干净，别让旧题名飘在新棋盘上
		_toastTween?.Kill();
		_toastLabel.Visible = false;

		_puzzle = def;
		_puzzleIndex = index;
		_puzzleKeys = GoPuzzles.Points(def.Keys, _n);

		foreach (int p in GoPuzzles.Points(def.Black, _n))
			_board[p] = Black;
		foreach (int p in GoPuzzles.Points(def.White, _n))
			_board[p] = White;
		_prevHash = 0;           // 摆出来的局面不算「上一手」，别误判打劫
		_turn = Black;
		_passes = 0;
		_capBlack = 0;
		_capWhite = 0;
		_moveCount = 0;
		_lastMove = -1;
		_undo.Clear();

		BuildPuzzleZone();
		_aiWait = 0f;
		UpdateScoringUi();
		UpdateHud();
		Redraw();
		GD.Print($"[Go] puzzle \"{def.Name}\" {_n} 路 · {def.Goal} · 答案点 {_puzzleKeys.Length} 个");
	}

	/// <summary>
	/// 算出「局部战场」：所有初始棋子 + 答案点，外扩一格。
	/// 这个矩形里的空点电脑一律不下 —— 它照常在别处应手、照常布局，
	/// 但不抢答案、也不去救那块被打吃的白棋，多步题才不会被机器人随手搅黄。
	/// </summary>
	private void BuildPuzzleZone()
	{
		_puzzleAiBan = new bool[_board.Length];
		int x0 = _n, y0 = _n, x1 = -1, y1 = -1;
		for (int i = 0; i < _board.Length; i++)
		{
			if (_board[i] == Empty)
				continue;
			int x = i % _n, y = i / _n;
			x0 = Mathf.Min(x0, x); y0 = Mathf.Min(y0, y);
			x1 = Mathf.Max(x1, x); y1 = Mathf.Max(y1, y);
		}
		foreach (int p in _puzzleKeys)
		{
			int x = p % _n, y = p / _n;
			x0 = Mathf.Min(x0, x); y0 = Mathf.Min(y0, y);
			x1 = Mathf.Max(x1, x); y1 = Mathf.Max(y1, y);
		}
		x0 = Mathf.Max(0, x0 - 1); y0 = Mathf.Max(0, y0 - 1);
		x1 = Mathf.Min(_n - 1, x1 + 1); y1 = Mathf.Min(_n - 1, y1 + 1);
		_puzzleZoneX0 = x0; _puzzleZoneY0 = y0;
		_puzzleZoneX1 = x1; _puzzleZoneY1 = y1;
		for (int y = y0; y <= y1; y++)
			for (int x = x0; x <= x1; x++)
				_puzzleAiBan[y * _n + x] = true;
	}

	private bool PuzzleAchieved()
	{
		if (_puzzle == null)
			return false;
		if (_puzzle.Kind == PuzzleGoal.Capture)
			return _capBlack >= _puzzle.Target;
		if (_puzzleKeys.Length == 0)
			return false;
		foreach (int p in _puzzleKeys)
			if (_board[p] != Black)
				return false;
		return true;
	}

	/// <summary>玩家每落一子检查一次；过关只报一次，超出手数给个「再试」提示但不打断对局。</summary>
	private void CheckPuzzle()
	{
		if (_puzzle == null || _puzzleSolved || _lan != null)
			return;
		_puzzleMoves++;
		if (PuzzleAchieved())
		{
			_puzzleSolved = true;
			ShowToast("✓ 过关啦 · 还能继续下完这盘");
			GD.Print($"[Go] puzzle solved: {_puzzle.Name} in {_puzzleMoves} 手");
		}
		else if (_puzzleMoves >= _puzzle.MaxMoves)
		{
			ShowToast("这局没解开 · 点「重开本题」再试一次");
		}
		UpdateHud();
	}

	// ===================== 续玩：快照 / 恢复 =====================

	/// <summary>纯数据快照。已结算 / 设置页 / 联机局返回空字典 = 不写盘。</summary>
	public Godot.Collections.Dictionary Snapshot()
	{
		if (_phase == Phase.Done || _phase == Phase.Setup || _lan != null)
			return new Godot.Collections.Dictionary();

		var board = new Godot.Collections.Array();
		foreach (sbyte v in _board)
			board.Add((int)v);
		var dead = new Godot.Collections.Array();
		foreach (bool d in _dead)
			dead.Add(d);

		return new Godot.Collections.Dictionary
		{
			{ "v", 1 },
			{ "level", _levelIndex },
			{ "puzzle", _puzzleIndex },
			{ "board", board },
			{ "dead", dead },
			{ "turn", _turn },
			{ "passes", _passes },
			{ "capB", _capBlack },
			{ "capW", _capWhite },
			{ "moves", _moveCount },
			{ "last", _lastMove },
			{ "phase", (int)_phase },
			{ "hashHi", (int)(_prevHash >> 32) },
			{ "hashLo", (int)(_prevHash & 0xFFFFFFFFUL) },
			{ "scoreB", _blackScore },
			{ "scoreW", _whiteScore },
			{ "pMoves", _puzzleMoves },
			{ "solved", _puzzleSolved },
		};
	}

	/// <summary>
	/// 恢复一律先走自己的 StartGame / StartPuzzle 入口把场景重建好，再覆盖盘面数据；
	/// 不在这里另写一套「直接摆节点」的代码。
	/// </summary>
	public void Restore(Godot.Collections.Dictionary data)
	{
		int puzzle = ResumeState.Int(data, "puzzle", -1);
		if (puzzle >= 0 && puzzle < GoPuzzles.All.Length)
			StartPuzzle(puzzle);
		else
			StartGame(ResumeState.Int(data, "level"));

		var board = ResumeState.Arr(data, "board");
		if (board.Count == _board.Length)
			for (int i = 0; i < _board.Length; i++)
				_board[i] = (sbyte)Mathf.Clamp(ResumeState.Int(board[i]), 0, 2);

		var dead = ResumeState.Arr(data, "dead");
		if (dead.Count == _dead.Length)
			for (int i = 0; i < _dead.Length; i++)
				_dead[i] = dead[i].AsBool();

		_turn = ResumeState.Int(data, "turn", Black) == White ? White : Black;
		_passes = Mathf.Clamp(ResumeState.Int(data, "passes"), 0, 2);
		_capBlack = ResumeState.Int(data, "capB");
		_capWhite = ResumeState.Int(data, "capW");
		_moveCount = ResumeState.Int(data, "moves");
		_lastMove = ResumeState.Int(data, "last", -1);
		if (_lastMove < -1 || _lastMove >= _board.Length)
			_lastMove = -1;
		_phase = (Phase)Mathf.Clamp(ResumeState.Int(data, "phase", (int)Phase.Play),
			(int)Phase.Play, (int)Phase.Scoring);
		_prevHash = ((ulong)(uint)ResumeState.Int(data, "hashHi") << 32)
					| (uint)ResumeState.Int(data, "hashLo");
		_blackScore = ResumeState.Float(data, "scoreB");
		_whiteScore = ResumeState.Float(data, "scoreW");
		_puzzleMoves = ResumeState.Int(data, "pMoves");
		_puzzleSolved = ResumeState.Bool(data, "solved");
		_undo.Clear();
		_aiWait = 0f;
		_setup.Visible = false;
		_result.Visible = false;

		if (_phase == Phase.Scoring)
			ComputeOwners();     // 里面会重建 _owner，数子预览才画得出来
		UpdateScoringUi();
		UpdateHud();
		Redraw();
		GD.Print($"[Go] resumed: {_n} 路 · puzzle={_puzzleIndex} phase={_phase} " +
				 $"moves={_moveCount} turn={_turn} cap={_capBlack}/{_capWhite}");
	}

	private void BuildZobrist()
	{
		// 固定种子：同一个局面的哈希每次运行都一样，出问题好复现
		var rng = new Random(20261107);
		for (int i = 0; i < _zob.Length; i++)
			_zob[i] = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
	}

	private ulong Hash(sbyte[] b)
	{
		ulong h = 1469598103934665603UL;
		int total = b.Length;
		for (int i = 0; i < total; i++)
		{
			sbyte v = b[i];
			if (v != Empty)
				h ^= _zob[i * 2 + (v - 1)];
		}
		return h;
	}

	// ===================== 规则 =====================
	//
	// 棋盘用一维 sbyte 数组：0 空 / 1 黑 / 2 白。
	// 提子和气都靠临时 flood fill 算，没有缓存棋块 —— 19 路只有 361 个点，
	// 每次重算比维护一套「棋块链表 + 增量更新」错得少。

	/// <summary>
	/// 数一块棋的「子数」和「气数」。<paramref name="stones"/> 不为 null 时把这块棋的坐标也收进去
	/// （AI 判相邻棋块、自测摆盘都要用）。
	/// </summary>
	private void MarkGroup(sbyte[] b, int start, out int size, out int libs, List<int>? stones = null)
	{
		_stampId++;
		int mark = _stampId;
		sbyte color = b[start];
		size = 0;
		libs = 0;

		_flood.Clear();
		_flood.Push(start);
		_stamp[start] = mark;

		while (_flood.Count > 0)
		{
			int i = _flood.Pop();
			size++;
			stones?.Add(i);
			int x = i % _n, y = i / _n;
			for (int k = 0; k < 4; k++)
			{
				int nx = x + Dx[k], ny = y + Dy[k];
				if ((uint)nx >= (uint)_n || (uint)ny >= (uint)_n)
					continue;
				int j = ny * _n + nx;
				sbyte v = b[j];
				if (v == Empty)
				{
					// 同一个空点可能被这块棋的多个子邻接，去重
					if (_libStamp[j] != mark)
					{
						_libStamp[j] = mark;
						libs++;
					}
				}
				else if (v == color && _stamp[j] != mark)
				{
					_stamp[j] = mark;
					_flood.Push(j);
				}
			}
		}
	}

	/// <summary>把一整块棋从棋盘上拿掉（调用前已经确认它没气）。</summary>
	private void RemoveGroup(sbyte[] b, int start, List<int>? removed)
	{
		sbyte color = b[start];
		b[start] = Empty;
		removed?.Add(start);

		_flood.Clear();
		_flood.Push(start);
		while (_flood.Count > 0)
		{
			int i = _flood.Pop();
			int x = i % _n, y = i / _n;
			for (int k = 0; k < 4; k++)
			{
				int nx = x + Dx[k], ny = y + Dy[k];
				if ((uint)nx >= (uint)_n || (uint)ny >= (uint)_n)
					continue;
				int j = ny * _n + nx;
				if (b[j] != color)
					continue;
				b[j] = Empty;
				removed?.Add(j);
				_flood.Push(j);
			}
		}
	}

	/// <summary>
	/// 在 <paramref name="b"/> 上模拟一手：落子 → 提掉没气的对方棋块 → 检查自己的气。
	/// 不合法（有子 / 自杀）返回 false，此时 <paramref name="b"/> 已经被改脏了，
	/// 调用方必须丢掉它（我们每次都传 clone 进来，就是为了这个）。
	/// 打劫不在这里判 —— 那要看全局哈希，见 <see cref="TryPlay"/>。
	/// </summary>
	private bool SimulateMove(sbyte[] b, int p, sbyte color, out int captured, out int libs,
		out int groupSize, List<int>? removed = null)
	{
		captured = 0;
		libs = 0;
		groupSize = 0;
		if (b[p] != Empty)
			return false;

		b[p] = color;
		sbyte opp = color == Black ? White : Black;
		int x = p % _n, y = p / _n;

		for (int k = 0; k < 4; k++)
		{
			int nx = x + Dx[k], ny = y + Dy[k];
			if ((uint)nx >= (uint)_n || (uint)ny >= (uint)_n)
				continue;
			int j = ny * _n + nx;
			if (b[j] != opp)
				continue;

			MarkGroup(b, j, out int sz, out int lb);
			if (lb != 0)
				continue;
			captured += sz;
			RemoveGroup(b, j, removed);
		}

		MarkGroup(b, p, out groupSize, out libs);
		return libs > 0; // 没气 = 自杀
	}

	/// <summary>真眼：四个正交邻点和四个斜邻点全是自己的子（或棋盘外）。</summary>
	private bool IsTrueEye(sbyte[] b, int p, int color)
	{
		int x = p % _n, y = p / _n;

		for (int k = 0; k < 4; k++)
		{
			int nx = x + Dx[k], ny = y + Dy[k];
			if ((uint)nx >= (uint)_n || (uint)ny >= (uint)_n)
				continue;
			if (b[ny * _n + nx] != color)
				return false;
		}

		for (int dy = -1; dy <= 1; dy++)
		for (int dx = -1; dx <= 1; dx++)
		{
			if (dx == 0 && dy == 0)
				continue;
			int nx = x + dx, ny = y + dy;
			if ((uint)nx >= (uint)_n || (uint)ny >= (uint)_n)
				continue;
			if (b[ny * _n + nx] != color)
				return false;
		}
		return true;
	}

	/// <summary>
	/// 从某个空点出发，扫一遍它所在的空区：面积多大、边界上都有谁。
	/// <paramref name="borders"/> 是位标记：1 = 挨着黑，2 = 挨着白。
	/// </summary>
	private void ScanRegion(sbyte[] b, int start, out int size, out int borders, List<int>? points = null)
	{
		_stampId++;
		int mark = _stampId;
		size = 0;
		borders = 0;

		_flood.Clear();
		_flood.Push(start);
		_stamp[start] = mark;

		while (_flood.Count > 0)
		{
			int i = _flood.Pop();
			size++;
			points?.Add(i);
			int x = i % _n, y = i / _n;
			for (int k = 0; k < 4; k++)
			{
				int nx = x + Dx[k], ny = y + Dy[k];
				if ((uint)nx >= (uint)_n || (uint)ny >= (uint)_n)
					continue;
				int j = ny * _n + nx;
				sbyte v = b[j];
				if (v == Empty)
				{
					if (_stamp[j] != mark)
					{
						_stamp[j] = mark;
						_flood.Push(j);
					}
				}
				else
				{
					borders |= v == Black ? 1 : 2;
				}
			}
		}
	}

	private void PushUndo()
	{
		_undo.Add(new Snap
		{
			Board = (sbyte[])_board.Clone(),
			PrevHash = _prevHash,
			Turn = _turn,
			Passes = _passes,
			CapBlack = _capBlack,
			CapWhite = _capWhite,
			LastMove = _lastMove,
			MoveCount = _moveCount,
		});
		if (_undo.Count > 160)
			_undo.RemoveAt(0);
	}

	private void RestoreLast()
	{
		if (_undo.Count == 0)
			return;
		var s = _undo[^1];
		_undo.RemoveAt(_undo.Count - 1);
		_board = s.Board;
		_prevHash = s.PrevHash;
		_turn = s.Turn;
		_passes = s.Passes;
		_capBlack = s.CapBlack;
		_capWhite = s.CapWhite;
		_lastMove = s.LastMove;
		_moveCount = s.MoveCount;
	}

	/// <summary>真正落一手。返回 false 表示这里不能下（有子 / 自杀 / 打劫）。</summary>
	private bool TryPlay(int p, sbyte color)
	{
		if (_phase != Phase.Play)
			return false;
		if (p < 0 || p >= _board.Length || _board[p] != Empty)
			return false;

		var test = (sbyte[])_board.Clone();
		var removed = new List<int>();
		if (!SimulateMove(test, p, color, out int cap, out int libs, out int gsize, removed))
			return false;
		if (Hash(test) == _prevHash)
			return false; // 打劫：提回来会复现上上手的局面

		PushUndo();
		_prevHash = Hash(_board);
		_board = test;

		_lastMove = p;
		_passes = 0;
		_moveCount++;
		if (color == Black)
			_capBlack += cap;
		else
			_capWhite += cap;
		_turn = color == Black ? White : Black;
		_aiWait = 0f;
		_aiThinking = _turn == White;

		var pc = PointCenter(p);
		_pops.Add(new Pop { Pos = pc, Age = 0f, Tint = color == Black ? new Color(1f, 1f, 1f, 0.55f) : new Color("#7a4a18") });
		foreach (int i in removed)
			_rings.Add(new Ring { Pos = PointCenter(i), Age = 0f, Tint = color == Black ? new Color("#ffd77a") : new Color("#bcd9ff") });

		PlaySfx(1.9f - 0.05f * Math.Min(cap, 6), -22f);
		UpdateHud();
		Redraw();
		if (color == Black)
			CheckPuzzle();   // 残局的过关判定：只在玩家（黑）落子后走

		GD.Print($"[Go] {(color == Black ? "黑" : "白")} {Coord(p)} cap={cap} libs={libs} size={gsize} " +
				 $"moves={_moveCount}");
		return true;
	}

	/// <summary>停一手。双方连续停一手就进「标死子 + 数子」。</summary>
	private void PassMove(int color)
	{
		if (_phase != Phase.Play || _turn != color)
			return;

		PushUndo();
		_prevHash = Hash(_board);
		_passes++;
		_moveCount++;
		_lastMove = -1;
		_turn = color == Black ? White : Black;
		_aiWait = 0f;
		_aiThinking = _turn == White;

		GD.Print($"[Go] {(color == Black ? "黑" : "白")} 停一手（连续 {_passes}）");

		if (_passes >= 2)
		{
			if (IsLan)
			{
				// 联机局没法「一个人点死子」：两个人都得同意哪块死了，这套协商不值得做。
				// 直接按盘面数子，有争议就点「再来一局」。
				_phase = Phase.Scoring; // Settle 的前置状态
				Settle();
			}
			else
			{
				EnterScoring();
			}
		}
		else
		{
			UpdateHud();
			Redraw();
		}
	}

	private void EnterScoring()
	{
		_phase = Phase.Scoring;
		_dead = new bool[_board.Length];
		ComputeOwners();
		_aiThinking = false;
		UpdateScoringUi();
		UpdateHud();
		Redraw();
		GD.Print("[Go] 双方停一手 -> 进入标记死子");
	}

	private void ResumePlay()
	{
		if (_phase != Phase.Scoring)
			return;
		_phase = Phase.Play;
		_dead = new bool[_board.Length];
		_owner = new sbyte[_board.Length];
		_passes = 0;
		_prevHash = 0;
		_turn = Black;
		_aiWait = 0f;
		UpdateScoringUi();
		UpdateHud();
		Redraw();
		GD.Print("[Go] 继续下棋");
	}

	/// <summary>结算：按「子 + 空」数子法算（死子先拿掉）。</summary>
	private void Settle()
	{
		if (_phase != Phase.Scoring)
			return;

		ComputeScore(out _blackScore, out _whiteScore, out int neutral);
		_phase = Phase.Done;
		_aiThinking = false;
		UpdateScoringUi();
		UpdateHud();
		ShowResult(neutral);
		Redraw();
		GD.Print($"[Go] 结算：黑 {_blackScore:0.0} / 白 {_whiteScore:0.0}（含贴目 " +
				 $"{Levels[_levelIndex].Komi:0.0}）· 单官 {neutral}");
	}

	private void ComputeScore(out float black, out float white, out int neutral)
	{
		int total = _n * _n;
		var b = (sbyte[])_board.Clone();
		for (int i = 0; i < total; i++)
			if (_dead[i])
				b[i] = Empty;

		int bs = 0, ws = 0;
		for (int i = 0; i < total; i++)
		{
			if (b[i] == Black) bs++;
			else if (b[i] == White) ws++;
		}

		int bt = 0, wt = 0;
		neutral = 0;
		// 注意：不能拿 _stamp / _stampId 当「这块空扫过了」的标记 ——
		// ScanRegion 每扫一块就把 _stampId 加一，早先扫过的块用的是旧编号，
		// 外层再比较就永远不相等（初版就是这么把每块空数了 9 遍的）。
		var seen = new bool[total];
		var pts = new List<int>();
		for (int i = 0; i < total; i++)
		{
			if (b[i] != Empty || seen[i])
				continue;
			pts.Clear();
			ScanRegion(b, i, out int size, out int borders, pts);
			foreach (int j in pts)
				seen[j] = true;
			if (borders == 1) bt += size;
			else if (borders == 2) wt += size;
			else neutral += size;
		}

		black = bs + bt;
		white = ws + wt + Levels[_levelIndex].Komi;

		GD.Print($"[Go] 数子：黑子 {bs} + 黑空 {bt} = {black:0.0}；" +
				 $"白子 {ws} + 白空 {wt} + 贴 {Levels[_levelIndex].Komi:0.0} = {white:0.0}；" +
				 $"单官 {neutral}");
	}

	/// <summary>算每个空点归谁（只为画小方块，和 ComputeScore 是同一套判据）。</summary>
	private void ComputeOwners()
	{
		int total = _n * _n;
		_owner = new sbyte[total];
		var b = (sbyte[])_board.Clone();
		for (int i = 0; i < total; i++)
			if (_dead[i])
				b[i] = Empty;

		var seen = new bool[total];
		var pts = new List<int>();
		for (int i = 0; i < total; i++)
		{
			if (b[i] != Empty || seen[i])
				continue;
			pts.Clear();
			ScanRegion(b, i, out int size, out int borders, pts);
			sbyte own = borders == 1 ? Black : borders == 2 ? White : Empty;
			if (own == Empty)
				continue;
			foreach (int j in pts)
			{
				seen[j] = true;
				_owner[j] = own;
			}
		}
	}

	/// <summary>点一下棋块 → 整块标成死子 / 取消。只数子阶段有效。</summary>
	private void ToggleDeadAt(int p)
	{
		if (_phase != Phase.Scoring || _board[p] == Empty)
			return;
		bool mark = !_dead[p];
		MarkGroup(_board, p, out int size, out int libs, _gbuf);
		foreach (int j in _gbuf)
			_dead[j] = mark;
		_gbuf.Clear();

		PlaySfx(mark ? 1.4f : 2.0f, -26f);
		ComputeOwners();
		UpdateScoringUi();
		Redraw();
		GD.Print($"[Go] 标记死子 {Coord(p)} × {size} -> {mark}");
	}

	// ===================== 启发式机器人 =====================
	//
	// 一句话：给每个能下的点打分，取最高分。所有分数都是「相对好坏」，
	// 不需要绝对值有意义 —— 只有和阈值（能不能停一手）比较时才有意义。
	//
	// 优先级从高到低：
	//   1. 能提子（+850 起，每子 +150）
	//   2. 能救自己只剩一口气的棋（+280 起）
	//   3. 能把对方打成只剩一口气（+80 起）
	//   4. 自己下完气多（每口气 +8）
	//   5. 别自紧一气（-1200）、别填自己的真眼（-1200）
	//   6. 别往自己的空里填子（-70 起，按空的大小加罚）
	//   7. 位置感：19 路喜欢三四线、讨厌一线（开局权重最大，中后盘衰减）
	//   8. 跟着对方上一手走（距离 2 最好，距离 1 次之）
	//   9. 连自己的棋（每多连一块 +15）、贴着对方的棋（+3）
	//  10. 一点随机，避免每局都走成一模一样

	private int ChooseAiMove(sbyte color, out int bestEval)
	{
		bestEval = int.MinValue;
		int n = _n;
		int total = n * n;

		int stones = 0;
		for (int i = 0; i < total; i++)
			if (_board[i] != Empty)
				stones++;

		// 开局把全盘都算一遍（不然对方下在天元，电脑就只会在天元附近扎堆）；
		// 中后盘只看有子附近 4 格，19 路也够快。
		bool opening = stones < 8;
		var cand = new List<int>();
		for (int y = 0; y < n; y++)
		for (int x = 0; x < n; x++)
		{
			int p = y * n + x;
			if (_board[p] != Empty)
				continue;
			// 残局：局部战场留给玩家解题，电脑只在别处应手（见 BuildPuzzleZone）
			if (_puzzle != null && _puzzleAiBan[p])
				continue;
			if (!opening && !NearStone(x, y, 4))
				continue;
			cand.Add(p);
		}
		if (cand.Count == 0)
			return -1;

		BuildDistances(Black, _distBlack);
		BuildDistances(White, _distWhite);

		var test = new sbyte[total];
		int best = -1;

		foreach (int p in cand)
		{
			Array.Copy(_board, test, total);
			int x = p % n, y = p / n;

			// 落子前先看一圈：相邻的己方棋块里，有没有只剩一口气的（能救就值钱）
			_gbuf.Clear();
			int ownGroups = 0, ownAdj = 0, oppAdj = 0, rescueSize = 0;
			for (int k = 0; k < 4; k++)
			{
				int nx = x + Dx[k], ny = y + Dy[k];
				if ((uint)nx >= (uint)n || (uint)ny >= (uint)n)
					continue;
				int j = ny * n + nx;
				if (_board[j] == color)
				{
					ownAdj++;
					if (_gbuf.Contains(j))
						continue;
					MarkGroup(_board, j, out int gsz, out int glb, _gbuf);
					ownGroups++;
					if (glb == 1)
						rescueSize += gsz;
				}
				else if (_board[j] != Empty)
				{
					oppAdj++;
				}
			}

			if (!SimulateMove(test, p, color, out int cap, out int libs, out int gsize))
				continue;
			if (Hash(test) == _prevHash)
				continue; // 打劫

			int score = 0;

			// ① 提子：几乎永远是好棋
			if (cap > 0)
				score += 850 + 150 * cap;

			// ② 自己的气
			if (libs <= 1)
				score -= 1200;          // 自紧一气，等于送死
			else if (libs == 2)
				score -= 45;            // 只有两口气，容易被追着打
			score += Math.Min(libs, 6) * 8;
			if (gsize >= 2)
				score += 3 * Math.Min(gsize, 6);

			// ③ 救子 / 打吃
			if (rescueSize > 0 && libs >= 2)
				score += 280 + 30 * rescueSize;
			int atariStones = CountOpponentAtari(test, p, color);
			if (atariStones > 0)
				score += 80 + 22 * atariStones;

			// ④ 别填自己的眼 / 自己的空
			if (IsTrueEye(_board, p, color))
				score -= 1200;
			else
			{
				ScanRegion(_board, p, out int rsize, out int borders);
				int myBit = color == Black ? 1 : 2;
				if (borders == myBit)
					score -= 70 + 26 * rsize;
			}

			// ⑤ 位置感
			int edge = Math.Min(Math.Min(x, y), Math.Min(n - 1 - x, n - 1 - y));
			score += LineBonus(edge, n, opening ? 1f : 0.4f);
			if (opening && NearStar(x, y))
				score += 32;
			if (!opening)
			{
				int near = NearestStoneDistance(x, y, 6);
				if (near > 6)
					score -= 10;
			}

			// ⑤b 势力范围：往「自己这一片的外沿」下（离自己的子 2~4 格、
			//     并且比对方更近）。没有这一项，电脑会一直贴着自己最后一颗子长，
			//     整盘走成一条蛇（改动前 AI 自战就是这个样子）。
			int myD = color == Black ? _distBlack[p] : _distWhite[p];
			int opD = color == Black ? _distWhite[p] : _distBlack[p];
			if (myD >= 2 && myD <= 4)
				score += 12;
			else if (myD == 1)
				score -= 6;
			else if (myD > 5)
				score -= 8;
			score += Math.Clamp(opD - myD, -4, 6) * 4;

			// ⑤c 别把子全堆在自己已经占住的地方：半径 3 内自己子越多越不值得下，
			//     对方的子多反而值得去争（不然整盘只会从自己的棋堆边缘往外糊）。
			int ownNear = CountNear(x, y, 3, color);
			int oppNear = CountNear(x, y, 3, color == Black ? White : Black);
			score -= Math.Min(30, 3 * ownNear);
			score += Math.Min(10, 2 * oppNear);

			// ⑥ 跟着上一手走（局部应一手）
			if (_lastMove >= 0)
			{
				int lx = _lastMove % n, ly = _lastMove / n;
				int d = Math.Max(Math.Abs(x - lx), Math.Abs(y - ly));
				score += d switch { 1 => 4, 2 => 10, 3 => 4, > 6 => -4, _ => 0 };
			}

			// ⑦ 连自己的棋 / 贴身
			if (ownGroups >= 2)
				score += 14 * (ownGroups - 1);
			score += 2 * ownAdj;
			score += 3 * oppAdj;

			// ⑧ 开局别把一个角堆成一片
			if (opening)
				score -= 6 * Math.Max(0, CountOwnInQuadrant(x, y, color) - 2);

			score += _aiRng.Next(0, 6);

			if (score > bestEval)
			{
				bestEval = score;
				best = p;
			}
		}

		if (best < 0)
			return -1;

		// 停一手的判据：对方刚停一手、自己也没有像样的点 → 跟着停（不然会一直填单官）
		if (_passes > 0 && bestEval < 50)
			return -1;
		if (bestEval < 10)
			return -1;

		return best;
	}

	private void AiTurn()
	{
		if (_phase != Phase.Play || _turn != White)
			return;
		_aiThinking = false;

		ulong t0 = Time.GetTicksMsec();
		int p = ChooseAiMove(White, out int ev);
		int ms = (int)(Time.GetTicksMsec() - t0);
		_aiDelayMs = ms;

		if (p < 0)
		{
			GD.Print($"[Go] AI 停一手（eval={ev}，{ms}ms）");
			PassMove(White);
			return;
		}

		if (!TryPlay(p, White))
		{
			// 理论上不会发生（候选点都模拟过），真发生了就宁可停一手，
			// 也不要卡在「轮到白棋」里把整盘棋挂死
			GD.PushError($"[Go] AI 选了一个下不了的点 {Coord(p)}，改为停一手");
			PassMove(White);
			return;
		}

		GD.Print($"[Go] AI {Coord(p)} eval={ev} 用时 {ms}ms");
	}

	/// <summary>
	/// 全盘 BFS：每个空点到最近一颗 <paramref name="color"/> 棋的距离。
	/// 对手的棋子会挡路（那边的势力范围），自己的棋子是起点。
	/// 用途：让电脑懂得「往自己势力范围的外沿下」，而不是贴着自己长成一条蛇。
	/// </summary>
	private void BuildDistances(sbyte color, int[] dst)
	{
		int total = _n * _n;
		for (int i = 0; i < total; i++)
			dst[i] = 999;

		_bfs.Clear();
		for (int i = 0; i < total; i++)
			if (_board[i] == color)
			{
				dst[i] = 0;
				_bfs.Enqueue(i);
			}

		while (_bfs.Count > 0)
		{
			int i = _bfs.Dequeue();
			int d = dst[i] + 1;
			int x = i % _n, y = i / _n;
			for (int k = 0; k < 4; k++)
			{
				int nx = x + Dx[k], ny = y + Dy[k];
				if ((uint)nx >= (uint)_n || (uint)ny >= (uint)_n)
					continue;
				int j = ny * _n + nx;
				if (_board[j] != Empty)
					continue;
				if (dst[j] <= d)
					continue;
				dst[j] = d;
				_bfs.Enqueue(j);
			}
		}
	}

	private bool NearStone(int x, int y, int dist)
	{
		for (int dy = -dist; dy <= dist; dy++)
		for (int dx = -dist; dx <= dist; dx++)
		{
			int nx = x + dx, ny = y + dy;
			if ((uint)nx >= (uint)_n || (uint)ny >= (uint)_n)
				continue;
			if (_board[ny * _n + nx] != Empty)
				return true;
		}
		return false;
	}

	/// <summary>离最近一颗子有多远（切比雪夫距离，最多找到 <paramref name="cap"/>）。</summary>
	private int NearestStoneDistance(int x, int y, int cap)
	{
		for (int d = 1; d <= cap; d++)
		{
			for (int dy = -d; dy <= d; dy++)
			for (int dx = -d; dx <= d; dx++)
			{
				if (Math.Abs(dx) != d && Math.Abs(dy) != d)
					continue; // 只看这一圈
				int nx = x + dx, ny = y + dy;
				if ((uint)nx >= (uint)_n || (uint)ny >= (uint)_n)
					continue;
				if (_board[ny * _n + nx] != Empty)
					return d;
			}
		}
		return cap + 1;
	}

	private int CountNear(int x, int y, int r, sbyte color)
	{
		int c = 0;
		for (int dy = -r; dy <= r; dy++)
		{
			int ny = y + dy;
			if ((uint)ny >= (uint)_n)
				continue;
			for (int dx = -r; dx <= r; dx++)
			{
				int nx = x + dx;
				if ((uint)nx >= (uint)_n)
					continue;
				if (_board[ny * _n + nx] == color)
					c++;
			}
		}
		return c;
	}

	private int CountOwnInQuadrant(int x, int y, int color)
	{
		int half = _n / 2;
		int x0 = x < half ? 0 : half, x1 = x < half ? half : _n;
		int y0 = y < half ? 0 : half, y1 = y < half ? half : _n;
		int c = 0;
		for (int yy = y0; yy < y1; yy++)
		for (int xx = x0; xx < x1; xx++)
			if (_board[yy * _n + xx] == color)
				c++;
		return c;
	}

	private int CountOpponentAtari(sbyte[] b, int p, sbyte color)
	{
		sbyte opp = color == Black ? White : Black;
		int x = p % _n, y = p / _n;
		int sum = 0;
		_gbuf.Clear();
		for (int k = 0; k < 4; k++)
		{
			int nx = x + Dx[k], ny = y + Dy[k];
			if ((uint)nx >= (uint)_n || (uint)ny >= (uint)_n)
				continue;
			int j = ny * _n + nx;
			if (b[j] != opp || _gbuf.Contains(j))
				continue;
			MarkGroup(b, j, out int sz, out int lb, _gbuf);
			if (lb == 1)
				sum += sz;
		}
		_gbuf.Clear();
		return sum;
	}

	private static int LineBonus(int edge, int n, float w)
	{
		int b = n >= 17
			? edge switch { 0 => -38, 1 => -8, 2 => 22, 3 => 18, 4 => 6, _ => 0 }
			: n >= 11
				? edge switch { 0 => -32, 1 => -2, 2 => 18, 3 => 14, _ => 2 }
				: edge switch { 0 => -26, 1 => 4, 2 => 16, 3 => 8, _ => 0 };
		return (int)(b * w);
	}

	private static (int X, int Y)[] StarPoints(int n) => n switch
	{
		9 => new[] { (2, 2), (6, 2), (4, 4), (2, 6), (6, 6) },
		13 => new[] { (3, 3), (9, 3), (6, 6), (3, 9), (9, 9) },
		19 => new[]
		{
			(3, 3), (9, 3), (15, 3),
			(3, 9), (9, 9), (15, 9),
			(3, 15), (9, 15), (15, 15),
		},
		_ => Array.Empty<(int, int)>(),
	};

	private bool NearStar(int x, int y)
	{
		foreach (var s in _stars)
			if (Math.Abs(s.X - x) <= 1 && Math.Abs(s.Y - y) <= 1)
				return true;
		return false;
	}

	// ===================== 主循环 =====================

	public override void _Process(double delta)
	{
		if (_lan != null)
		{
			_lan.Update((float)delta);
			foreach (LanNet.Ev ev in _lan.Drain())
				HandleLanEvent(ev);

			// 面板上的房间列表是「广播收来的」，隔一会儿刷新一次就够了。
			// RefreshLanPanel 内部会比签名，没变化就什么都不做，不会打断正在输入的光标。
			if (_lanPanel.Visible)
			{
				_lanUiCd -= (float)delta;
				if (_lanUiCd <= 0f)
				{
					_lanUiCd = 0.35f;
					RefreshLanPanel();
				}
			}
		}

		// 电脑回合：等一小会儿再落子，纯粹是手感（一按就应会显得很诡异）。
		// 联机时没有电脑，这一整段要跳过。
		if (_lan == null && _phase == Phase.Play && _turn == White)
		{
			_aiWait += (float)delta;
			if (_aiWait >= AiDelay)
			{
				_aiWait = 0f;
				AiTurn();
			}
		}

		bool anim = false;

		for (int i = _pops.Count - 1; i >= 0; i--)
		{
			_pops[i].Age += (float)delta;
			anim = true;
			if (_pops[i].Age >= PopLife)
				_pops.RemoveAt(i);
		}

		for (int i = _rings.Count - 1; i >= 0; i--)
		{
			_rings[i].Age += (float)delta;
			anim = true;
			if (_rings[i].Age >= RingLife)
				_rings.RemoveAt(i);
		}

		if (anim)
			Redraw();
	}

	// ===================== 输入 =====================

	/// <summary>
	/// 引擎开着 mouse→touch，一次点击会同时送进来「真事件」和一个 device = -1 的模拟事件。
	/// 两个都处理就会一手下一子（围棋更糟：第二下直接被当成「点已有子」弹提示）。
	/// </summary>
	private static bool IsEmulated(InputEvent e) => e.Device == -1;

	/// <summary>
	/// 必须是 _Input 而不是 _UnhandledInput：棋盘画在 Node2D 上，不是 Control，
	/// 而 _UnhandledInput 只在「没有 Control 认领这次点击」时才调用 ——
	/// 根节点是铺满全屏的 Control（默认 Stop），会把空白处的点击全吃掉，
	/// 表现就是点哪儿都没反应。靠 PointAt 的范围判定 + _phase 拦掉按钮上的点击。
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventKey key when key.Pressed && !key.Echo:
				if (key.Keycode == Key.Escape && _phase == Phase.Play)
				{
					PassMove(Black);
					GetViewport().SetInputAsHandled();
				}
				else if (key.Keycode == Key.Z && key.CtrlPressed)
				{
					Undo();
					GetViewport().SetInputAsHandled();
				}
				break;

			case InputEventMouseMotion mm when !IsEmulated(mm):
				SetHover(mm.Position);
				break;

			case InputEventMouseButton mb when !IsEmulated(mb) && mb.ButtonIndex == MouseButton.Left:
				if (mb.Pressed)
					OnTap(mb.Position);
				break;

			case InputEventScreenTouch st when !IsEmulated(st):
				if (st.Pressed)
					OnTap(st.Position);
				break;
		}
	}

	private void OnTap(Vector2 pos)
	{
		int p = PointAt(pos);
		if (p < 0)
			return;

		if (_lan != null)
		{
			// 联机的点法：能不能下、轮到谁，全交给 LanMove 判（它还要决定是本地下还是发请求）
			if (_phase == Phase.Scoring)
				ShowToast("联机局不标死子，按盘面数子");
			else
				LanMove(p);
			return;
		}

		if (_phase == Phase.Scoring)
		{
			ToggleDeadAt(p);
			return;
		}
		if (_phase != Phase.Play)
			return;
		if (_turn != Black)
		{
			ShowToast("等电脑下完这一手");
			return;
		}
		if (_board[p] != Empty)
		{
			ShowToast("这里已经有子了");
			return;
		}
		if (!TryPlay(p, Black))
			ShowToast("这一手不能下（自杀 / 打劫）");
	}

	private void SetHover(Vector2 pos)
	{
		bool mine = _lan != null ? _turn == _myColor : _turn == Black;
		int p = _phase == Phase.Play && mine ? PointAt(pos) : -1;
		if (p == _hover)
			return;
		_hover = p;
		Redraw();
	}

	/// <summary>屏幕坐标 → 交叉点下标；离得太远（落在两条线中间）返回 -1。</summary>
	private int PointAt(Vector2 pos)
	{
		if (_n < 2 || _cell <= 0f)
			return -1;

		float fx = (pos.X - _origin.X) / _cell;
		float fy = (pos.Y - _origin.Y) / _cell;
		int x = Mathf.RoundToInt(fx);
		int y = Mathf.RoundToInt(fy);
		if ((uint)x >= (uint)_n || (uint)y >= (uint)_n)
			return -1;

		var center = new Vector2(_origin.X + x * _cell, _origin.Y + y * _cell);
		if (pos.DistanceTo(center) > _cell * 0.62f)
			return -1;
		return y * _n + x;
	}

	private void Undo()
	{
		if (_lan != null)
		{
			LanUndo();
			return;
		}

		if (_phase == Phase.Scoring)
		{
			ResumePlay();
			return;
		}
		if (_phase != Phase.Play || _undo.Count == 0)
			return;

		// 当前轮到黑（电脑刚下完）→ 要退两手才是「你还没下」的状态；
		// 当前轮到白（你刚下完、电脑还没应）→ 退一手就够。
		int steps = _turn == Black && _undo.Count >= 2 ? 2 : 1;
		RollbackPlies(steps);
	}

	// ===================== 绘制 =====================

	private void Redraw() => _view.QueueRedraw();

	private Vector2 PointCenter(int p)
		=> new(_origin.X + (p % _n) * _cell, _origin.Y + (p / _n) * _cell);

	private string Coord(int p)
		=> p < 0 ? "pass" : $"({p % _n},{p / _n})";

	private void DrawBoard(CanvasItem ci)
	{
		if (_n < 2)
			return;

		float half = _cell * PanelPad;
		float gw = _cell * (_n - 1);
		var panel = new Rect2(_origin.X - half, _origin.Y - half, gw + half * 2f, gw + half * 2f);

		// 底板：浅木色 + 深色描边，外面再垫一层半透明暗板，把棋盘从背景里托出来
		ci.DrawStyleBox(_sbPanel, new Rect2(panel.Position - new Vector2(10f, 10f), panel.Size + new Vector2(20f, 20f)));
		ci.DrawStyleBox(_sbWood, panel);

		// 木纹：几条很淡的横带
		for (int i = 1; i <= 3; i++)
		{
			float y = panel.Position.Y + panel.Size.Y * i / 4f;
			ci.DrawLine(new Vector2(panel.Position.X + 10f, y), new Vector2(panel.End.X - 10f, y),
				new Color(0.42f, 0.26f, 0.10f, 0.07f), 7f, true);
		}

		// 网格
		var ink = new Color(0.32f, 0.20f, 0.09f, 0.82f);
		float lw = _n >= 17 ? 1.1f : _n >= 11 ? 1.4f : 1.8f;
		for (int i = 0; i < _n; i++)
		{
			float x = _origin.X + i * _cell;
			float y = _origin.Y + i * _cell;
			ci.DrawLine(new Vector2(x, _origin.Y), new Vector2(x, _origin.Y + gw), ink, lw, true);
			ci.DrawLine(new Vector2(_origin.X, y), new Vector2(_origin.X + gw, y), ink, lw, true);
		}
		ci.DrawRect(new Rect2(_origin.X, _origin.Y, gw, gw),
			new Color(0.26f, 0.16f, 0.07f, 0.9f), false, lw * 1.5f);

		// 星位
		foreach (var s in _stars)
			ci.DrawCircle(new Vector2(_origin.X + s.X * _cell, _origin.Y + s.Y * _cell),
				Mathf.Max(2.4f, _cell * 0.085f), ink);

		// 残局：把「局部战场」淡淡圈出来，帮小孩把注意力放到该看的地方。
		// 只圈范围、不点答案；过关之后转成绿色，一眼知道这道题拿下了。
		if (_puzzle != null && _puzzleZoneX1 >= _puzzleZoneX0)
		{
			var za = new Vector2(_origin.X + _puzzleZoneX0 * _cell - _cell * 0.42f,
								 _origin.Y + _puzzleZoneY0 * _cell - _cell * 0.42f);
			var zb = new Vector2(_origin.X + _puzzleZoneX1 * _cell + _cell * 0.42f,
								 _origin.Y + _puzzleZoneY1 * _cell + _cell * 0.42f);
			var zone = new Rect2(za, zb - za);
			// 蓝色在浅木色上比金色看得清；过关后转绿。底色只铺很淡一层，别糊住棋子。
			var tint = _puzzleSolved ? new Color("#2f9e5c") : new Color("#3f6fd8");
			ci.DrawRect(zone, new Color(tint.R, tint.G, tint.B, 0.06f));
			ci.DrawRect(zone, new Color(tint.R, tint.G, tint.B, 0.90f), false, 3f);
		}

		// 数子阶段：先把空点归属画出来（画在棋子下面，死子的位置不画）
		if (_phase is Phase.Scoring or Phase.Done)
		{
			for (int i = 0; i < _owner.Length; i++)
			{
				if (_owner[i] == Empty || _board[i] != Empty)
					continue;
				var c = PointCenter(i);
				float r = Mathf.Max(3f, _cell * 0.12f);
				if (_owner[i] == Black)
					ci.DrawRect(new Rect2(c - new Vector2(r, r), new Vector2(r * 2f, r * 2f)),
						new Color(0.10f, 0.09f, 0.08f, 0.55f));
				else
					ci.DrawRect(new Rect2(c - new Vector2(r, r), new Vector2(r * 2f, r * 2f)),
						new Color(1f, 1f, 1f, 0.75f));
			}
		}

		// 棋子
		for (int i = 0; i < _board.Length; i++)
		{
			if (_board[i] == Empty)
				continue;
			bool dead = _phase is Phase.Scoring or Phase.Done && _dead[i];
			DrawStone(ci, PointCenter(i), _cell * 0.46f, _board[i], false, dead, i == _lastMove);
			if (dead)
			{
				var c = PointCenter(i);
				float r = _cell * 0.22f;
				var x1 = new Color("#e0453f");
				ci.DrawLine(c - new Vector2(r, r), c + new Vector2(r, r), x1, 2.4f, true);
				ci.DrawLine(c + new Vector2(r, -r), c + new Vector2(-r, r), x1, 2.4f, true);
			}
		}

		// 鼠标悬停的落子预览（手机上没有，桌面端有）
		if (_hover >= 0 && _board[_hover] == Empty && _phase == Phase.Play && _turn == Black)
			DrawStone(ci, PointCenter(_hover), _cell * 0.46f, Black, true, false, false);

		// 最后一手：子被提掉之后留个小圈，不然「刚才下哪儿了」就丢了
		if (_lastMove >= 0 && _board[_lastMove] == Empty)
			ci.DrawArc(PointCenter(_lastMove), _cell * 0.30f, 0f, Mathf.Tau, 22,
				new Color(0.72f, 0.15f, 0.12f, 0.75f), 2.4f, true);

		// 落子 / 提子的光环
		foreach (var p in _pops)
		{
			float t = Mathf.Clamp(p.Age / PopLife, 0f, 1f);
			float r = _cell * (0.30f + 0.45f * t);
			ci.DrawCircle(p.Pos, r, new Color(p.Tint.R, p.Tint.G, p.Tint.B, 0.22f * (1f - t)));
		}
		foreach (var r in _rings)
		{
			float t = Mathf.Clamp(r.Age / RingLife, 0f, 1f);
			float rad = _cell * (0.40f + 0.70f * t);
			ci.DrawArc(r.Pos, rad, 0f, Mathf.Tau, 26,
				new Color(r.Tint.R, r.Tint.G, r.Tint.B, 0.85f * (1f - t)), 3f, true);
		}
	}

	private static void DrawStone(CanvasItem ci, Vector2 c, float r, sbyte color, bool ghost, bool dead, bool last)
	{
		float a = ghost ? 0.32f : dead ? 0.34f : 1f;

		// 影子：稍微往下偏一点，棋子才像「放在盘上」。
		// 半径要比棋子小一圈、透明度也低，否则在浅木色上会看成棋子外面套了一圈黑边。
		if (!ghost)
			ci.DrawCircle(c + new Vector2(0f, r * 0.10f), r * 0.92f, new Color(0.18f, 0.10f, 0.04f, 0.26f * a));

		if (color == Black)
		{
			// 三层同心圆做出「球面」的感觉：外圈近黑、往里一档档变亮，
			// 对比别拉太开 —— 只画两层的话看起来像棋子外面套了个黑环。
			ci.DrawCircle(c, r, new Color(0.055f, 0.055f, 0.07f, a));
			ci.DrawCircle(c + new Vector2(-r * 0.05f, -r * 0.07f), r * 0.88f, new Color(0.105f, 0.105f, 0.125f, a));
			ci.DrawCircle(c + new Vector2(-r * 0.12f, -r * 0.15f), r * 0.60f, new Color(0.175f, 0.18f, 0.21f, a));
			ci.DrawCircle(c + new Vector2(-r * 0.28f, -r * 0.32f), r * 0.20f, new Color(1f, 1f, 1f, 0.22f * a));
		}
		else
		{
			ci.DrawCircle(c, r, new Color(0.98f, 0.97f, 0.94f, a));
			ci.DrawArc(c, r * 0.96f, 0f, Mathf.Tau, 28,
				new Color(0.55f, 0.53f, 0.47f, a), Mathf.Max(1.2f, r * 0.075f), true);
			ci.DrawCircle(c + new Vector2(-r * 0.26f, -r * 0.30f), r * 0.26f, new Color(1f, 1f, 1f, 0.8f * a));
		}

		if (last && !ghost)
			ci.DrawCircle(c, r * 0.19f, color == Black ? new Color("#ff8f8f") : new Color("#d63a33"));
	}

	// ===================== 界面 =====================

	private void BuildTopBar()
	{
		_topBar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_topBar.OffsetLeft = 20;
		_topBar.OffsetTop = 20;
		_topBar.OffsetRight = -20;
		_topBar.OffsetBottom = 100;
		_topBar.AddThemeConstantOverride("separation", 14);

		GameArt.StyleButton(_homeButton, new Color("#8f7bff"), Colors.White, fontSize: 32);
		_homeButton.CustomMinimumSize = new Vector2(140, 80);
		_homeButton.Text = "返回";
		_homeButton.Pressed += () =>
		{
			// 走人之前先跟对面说一声，否则对方要等到超时才反应过来
			_lan?.Close();
			GetTree().ChangeSceneToFile(ScenePaths.Home);
		};

		GameArt.StyleButton(_setupButton, new Color("#4fa8ff"), Colors.White, fontSize: 32);
		_setupButton.CustomMinimumSize = new Vector2(140, 80);
		_setupButton.Text = "换难度";
		_setupButton.Pressed += ShowSetup;

		GameArt.StyleButton(_restartButton, new Color("#ff8f6b"), Colors.White, fontSize: 32);
		_restartButton.CustomMinimumSize = new Vector2(140, 80);
		_restartButton.Text = "重开";
		_restartButton.Pressed += () =>
		{
			if (_n > 1)
			{
				// 残局按「重开本题」，不是清成空盘 —— 题目就丢了
				if (_puzzle != null)
				{
					StartPuzzle(_puzzleIndex);
					return;
				}
				// 联机时「重开」不能只清自己这边：对面还摆着上一局，得说一声
				if (_lan != null)
					_lan.SendRestart();
				StartGame(_levelIndex);
			}
			else
			{
				ShowSetup();
			}
		};
	}

	private void BuildHud()
	{
		_hud = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
		_hud.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_hud.OffsetLeft = 24;
		_hud.OffsetTop = 106;
		_hud.OffsetRight = -24;
		_hud.OffsetBottom = 158;
		_hud.AddThemeConstantOverride("separation", 10);
		_ui.AddChild(_hud);

		_blackLabel = MakeHudLabel(HorizontalAlignment.Left, new Color("#ffd77a"));
		_turnLabel = MakeHudLabel(HorizontalAlignment.Center, Colors.White);
		_whiteLabel = MakeHudLabel(HorizontalAlignment.Right, new Color(1f, 1f, 1f, 0.86f));

		_levelLabel = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		GameArt.OutlineText(_levelLabel, new Color(1f, 1f, 1f, 0.78f), 25, 6);
		_levelLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_levelLabel.OffsetTop = 162;
		_levelLabel.OffsetBottom = 206;
		_ui.AddChild(_levelLabel);
	}

	private Label MakeHudLabel(HorizontalAlignment align, Color color)
	{
		var lb = new Label
		{
			HorizontalAlignment = align,
			MouseFilter = MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		GameArt.OutlineText(lb, color, 26, 6);
		_hud.AddChild(lb);
		return lb;
	}

	private void BuildBottom()
	{
		_bottom = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		_bottom.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		_bottom.GrowHorizontal = GrowDirection.Both;
		_bottom.GrowVertical = GrowDirection.Begin;
		_bottom.OffsetLeft = 70;
		_bottom.OffsetRight = -70;
		_bottom.OffsetTop = -168;
		_bottom.OffsetBottom = -90;
		_bottom.AddThemeConstantOverride("separation", 20);
		_ui.AddChild(_bottom);

		_undoButton = MakeBottomButton("悔棋", new Color(1f, 1f, 1f, 0.14f));
		_undoButton.Pressed += Undo;

		_passButton = MakeBottomButton("停一手", new Color("#4fa8ff"));
		_passButton.Pressed += () => PassMove(Black);

		_resumeButton = MakeBottomButton("继续下", new Color(1f, 1f, 1f, 0.14f));
		_resumeButton.Pressed += ResumePlay;

		_settleButton = MakeBottomButton("结算", new Color("#7ee787"));
		_settleButton.Pressed += Settle;

		_hintLabel = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		_hintLabel.AddThemeFontSizeOverride("font_size", 20);
		_hintLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.52f));
		_hintLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		_hintLabel.GrowHorizontal = GrowDirection.Both;
		_hintLabel.GrowVertical = GrowDirection.Begin;
		_hintLabel.OffsetLeft = 40;
		_hintLabel.OffsetRight = -40;
		_hintLabel.OffsetTop = -84;
		_hintLabel.OffsetBottom = -28;
		_ui.AddChild(_hintLabel);

		_toastLabel = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
			Visible = false,
		};
		GameArt.OutlineText(_toastLabel, new Color("#ffd77a"), 30, 8);
		_toastLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
		_toastLabel.GrowHorizontal = GrowDirection.Both;
		_toastLabel.OffsetLeft = -300;
		_toastLabel.OffsetRight = 300;
		_toastLabel.OffsetTop = 470;
		_toastLabel.OffsetBottom = 526;
		_ui.AddChild(_toastLabel);

		UpdateScoringUi();
	}

	private Button MakeBottomButton(string text, Color bg)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(270, 78) };
		GameArt.StyleButton(b, bg, Colors.White, radius: 24, fontSize: 30);
		_bottom.AddChild(b);
		return b;
	}

	/// <summary>下棋 / 数子两个阶段用的按钮不一样：数子时把「停一手」换成「结算」。</summary>
	private void UpdateScoringUi()
	{
		bool scoring = _phase == Phase.Scoring;
		bool done = _phase == Phase.Done;

		_undoButton.Visible = !scoring && !done;
		_passButton.Visible = !scoring && !done;
		_resumeButton.Visible = scoring;
		_settleButton.Visible = scoring;

		if (_phase == Phase.Scoring)
			_hintLabel.Text = IsLan
				? "双方各停一手就按盘面数子 · 联机局不标死子"
				: "点一下棋块 = 标为死子（再点取消）· 确认无误后按「结算」";
		else if (_phase == Phase.Done)
			_hintLabel.Text = "这一局结束了 · 按「重开」再来一局";
		else if (_puzzle != null)
			_hintLabel.Text = $"目标：{_puzzle.Goal} · {_puzzle.Tip} · 电脑不抢答案区";
		else
			_hintLabel.Text = IsLan
				? $"点交叉点落子 · 你执{(_myColor == Black ? "黑" : "白")} · 双方各停一手就数子"
				: "点交叉点落子 · 你执黑先下 · 双方各停一手就数子";
	}

	private void ShowToast(string text)
	{
		_toastLabel.Text = text;
		_toastLabel.Visible = true;
		_toastLabel.Modulate = new Color(1f, 1f, 1f, 0f);
		_toastTween?.Kill();
		_toastTween = CreateTween();
		_toastTween.TweenProperty(_toastLabel, "modulate:a", 1f, 0.10);
		_toastTween.TweenInterval(0.75);
		_toastTween.TweenProperty(_toastLabel, "modulate:a", 0f, 0.32);
		_toastTween.TweenCallback(Callable.From(() => _toastLabel.Visible = false));
	}

	private void UpdateHud()
	{
		if (IsLan)
		{
			// 联机时「黑/白」比「你/对手」更重要：执白的那个看的是右边那一栏
			bool meBlack = _myColor == Black;
			_blackLabel.Text = meBlack ? $"你 · 黑 · 提 {_capBlack}" : $"对手 · 黑 · 提 {_capBlack}";
			_whiteLabel.Text = meBlack ? $"对手 · 白 · 提 {_capWhite}" : $"你 · 白 · 提 {_capWhite}";
		}
		else
		{
			_blackLabel.Text = $"你 · 黑 · 提 {_capBlack}";
			_whiteLabel.Text = $"电脑 · 白 · 提 {_capWhite}";
		}

		bool meWin = IsLan ? (_blackScore > _whiteScore) == (_myColor == Black)
						   : _blackScore > _whiteScore;

		string turn = _phase switch
		{
			Phase.Setup => "选个难度",
			Phase.Scoring => IsLan ? "数子" : "点掉死子",
			Phase.Done => Mathf.IsEqualApprox(_blackScore, _whiteScore) ? "平局"
				: IsLan ? (meWin ? "你赢了" : "对手赢了")
				: (meWin ? "你赢了" : "电脑赢了"),
			_ => IsLan
				? (_turn == _myColor ? "轮到你" : "等对手落子")
				: (_turn == Black ? "轮到你" : (_aiThinking ? "电脑思考中…" : "电脑下棋")),
		};
		_turnLabel.Text = turn;

		if (_phase == Phase.Scoring)
		{
			// 数子阶段随手把预览分挂在副标题上，标完死子马上能看到变化
			ComputeScore(out float b, out float w, out _);
			_levelLabel.Text = $"预览  黑 {b:0.0} : 白 {w:0.0}（贴 {Levels[_levelIndex].Komi:0.0}）";
		}
		else if (_phase == Phase.Done)
		{
			_levelLabel.Text = $"黑 {_blackScore:0.0} : 白 {_whiteScore:0.0}（白含贴 " +
							   $"{Levels[_levelIndex].Komi:0.0}）";
		}
		else if (_puzzle != null)
		{
			_levelLabel.Text = _puzzleSolved
				? $"残局 · {_puzzle.Name} ✓ 已过关 · 继续下完这盘吧"
				: $"残局 · {_puzzle.Name} · {_puzzle.Goal} · {_puzzleMoves}/{_puzzle.MaxMoves} 手";
		}
		else
		{
			var def = Levels[_levelIndex];
			_levelLabel.Text = IsLan
				? $"联机 · {_n} 路 · 贴 {def.Komi:0.0} 目 · 对手 {Short(_lan?.PeerName ?? "?", 6)}"
				: $"{def.Name} · {_n} 路 · 贴 {def.Komi:0.0} 目";
		}
	}

	private void BuildSetup()
	{
		_setup = new Control { MouseFilter = MouseFilterEnum.Ignore };
		_setup.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_setup.Visible = false;
		_ui.AddChild(_setup);

		var dim = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.66f),
			MouseFilter = MouseFilterEnum.Stop, // 挡住底下的棋盘
		};
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_setup.AddChild(dim);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(0.10f, 0.08f, 0.05f, 0.98f), 34, new Color(1f, 1f, 1f, 0.20f));
		box.ContentMarginLeft = box.ContentMarginRight = 34;
		box.ContentMarginTop = box.ContentMarginBottom = 30;
		panel.AddThemeStyleboxOverride("panel", box);
		dim.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 14);
		col.SizeFlagsHorizontal = SizeFlags.ExpandFill;

		// 三档自由对局 + 残局题库 + 联机 + 返回，一屏装不下。
		// 整列放进固定高度的滚动容器：前三个难度按钮始终露在最上面，往下划就是残局。
		var scroll = new ScrollContainer
		{
			CustomMinimumSize = new Vector2(600, 880),
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
			VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
		};
		panel.AddChild(scroll);
		scroll.AddChild(col);

		var title = new Label { Text = "围 棋", HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(title, Colors.White, 60, 8);
		col.AddChild(title);

		var sub = new Label
		{
			Text = "你执黑先下 · 电脑应战 · 往下滑是残局挑战",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		sub.AddThemeFontSizeOverride("font_size", 22);
		sub.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.66f));
		col.AddChild(sub);

		for (int i = 0; i < Levels.Length; i++)
		{
			int idx = i;
			var b = new Button { CustomMinimumSize = new Vector2(560, 108) };
			b.Text = "";
			GameArt.StyleButton(b, new Color(1f, 1f, 1f, 0.09f), Colors.White, radius: 24,
				border: new Color(1f, 1f, 1f, 0.26f));
			b.AddThemeStyleboxOverride("hover", GameArt.MakeBox(new Color(1f, 1f, 1f, 0.18f), 24, new Color(1f, 1f, 1f, 0.5f)));
			b.AddThemeStyleboxOverride("pressed", GameArt.MakeBox(new Color(1f, 1f, 1f, 0.06f), 24, new Color(1f, 1f, 1f, 0.4f)));
			b.AddThemeStyleboxOverride("focus", GameArt.MakeBox(new Color(0f, 0f, 0f, 0f), 24));
			b.Pressed += () =>
			{
				GD.Print($"[Go] level -> {Levels[idx].Name}");
				StartGame(idx);
			};
			col.AddChild(b);
			_levelButtons[i] = b;

			// 按钮里的子节点必须 MouseFilter = Ignore，否则会把点击吃掉，按钮收不到 pressed
			var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
			row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			row.OffsetLeft = 26;
			row.OffsetRight = -26;
			row.OffsetTop = 12;
			row.OffsetBottom = -12;
			row.AddThemeConstantOverride("separation", 14);
			b.AddChild(row);

			var cellCol = new VBoxContainer
			{
				MouseFilter = MouseFilterEnum.Ignore,
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
			};
			cellCol.AddThemeConstantOverride("separation", 2);
			row.AddChild(cellCol);

			var name = new Label { Text = Levels[i].Name, MouseFilter = MouseFilterEnum.Ignore };
			GameArt.OutlineText(name, Levels[i].Tint, 38, 6);
			cellCol.AddChild(name);

			var info = new Label { MouseFilter = MouseFilterEnum.Ignore };
			info.AddThemeFontSizeOverride("font_size", 21);
			info.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.72f));
			cellCol.AddChild(info);
			_levelSubs[i] = info;
		}

		var puzzleTitle = new Label
		{
			Text = "残局挑战 · 和电脑边下边解题",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		GameArt.OutlineText(puzzleTitle, new Color("#ffd77a"), 34, 6);
		col.AddChild(puzzleTitle);

		for (int i = 0; i < GoPuzzles.All.Length; i++)
		{
			int idx = i;
			var def = GoPuzzles.All[i];
			var b = new Button { CustomMinimumSize = new Vector2(560, 96) };
			b.Text = "";
			GameArt.StyleButton(b, new Color(1f, 1f, 1f, 0.09f), Colors.White, radius: 22,
				border: new Color(1f, 1f, 1f, 0.26f));
			b.Pressed += () =>
			{
				GD.Print($"[Go] puzzle -> {def.Name}");
				// 残局是单机人机题，别把联机房间挂着进来
				if (_lan != null || _lanLive)
					TeardownLan();
				StartPuzzle(idx);
			};
			col.AddChild(b);
			_puzzleButtons[i] = b;

			// 按钮里的子节点一律 Ignore，否则点击被吃掉（难度按钮那边踩过）
			var cell = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
			cell.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			cell.OffsetLeft = 22;
			cell.OffsetRight = -22;
			cell.OffsetTop = 8;
			cell.OffsetBottom = -8;
			cell.AddThemeConstantOverride("separation", 2);
			b.AddChild(cell);

			var name = new Label
			{
				Text = $"{def.Name} · {def.Size} 路 · {def.Grade}",
				MouseFilter = MouseFilterEnum.Ignore,
			};
			GameArt.OutlineText(name, def.Tint, 30, 5);
			cell.AddChild(name);

			var info = new Label
			{
				Text = $"目标：{def.Goal}",
				MouseFilter = MouseFilterEnum.Ignore,
			};
			info.AddThemeFontSizeOverride("font_size", 19);
			info.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.70f));
			cell.AddChild(info);
		}

		var lanEntry = new Button
		{
			Text = "局域网对战 · 两台设备",
			CustomMinimumSize = new Vector2(560, 96),
		};
		GameArt.StyleButton(lanEntry, new Color("#3ddc97"), new Color("#08301f"), radius: 24, fontSize: 30);
		lanEntry.Pressed += OpenLanPanel;
		col.AddChild(lanEntry);

		_resumeGameButton = new Button { Text = "继续这一局", CustomMinimumSize = new Vector2(560, 84) };
		GameArt.StyleButton(_resumeGameButton, new Color("#7ee787"), new Color("#123018"), fontSize: 30);
		_resumeGameButton.Pressed += ResumeGame;
		col.AddChild(_resumeGameButton);

		var quit = new Button { Text = "返回首页", CustomMinimumSize = new Vector2(560, 84) };
		GameArt.StyleButton(quit, new Color("#8f7bff"), Colors.White, fontSize: 30);
		quit.Pressed += () =>
		{
			_lan?.Close();
			GetTree().ChangeSceneToFile(ScenePaths.Home);
		};
		col.AddChild(quit);
	}

	private void ShowSetup()
	{
		// 正在联机时按「换难度」= 拆房间走人；不拆的话对面会一直挂在那儿等
		if (_lanLive)
			TeardownLan();

		// 只有「下过棋、还没结束」才给「继续这一局」：空盘继续没意义
		if (_phase is Phase.Play or Phase.Scoring)
		{
			_resumeTo = _phase;
			_resumable = _moveCount > 0;
		}
		else
		{
			_resumable = false;
		}

		_phase = Phase.Setup;
		_aiWait = 0f;
		_aiThinking = false;
		_result.Visible = false;
		RefreshSetup();
		_setup.Visible = true;
		UpdateScoringUi();
		UpdateHud();
		Redraw();
	}

	private void ResumeGame()
	{
		if (!_resumable)
			return;
		_setup.Visible = false;
		_phase = _resumeTo;
		_aiWait = 0f;
		UpdateScoringUi();
		UpdateHud();
		Redraw();
	}

	private void RefreshSetup()
	{
		for (int i = 0; i < Levels.Length; i++)
		{
			var def = Levels[i];
			_levelSubs[i].Text = $"{def.Size} 路棋盘 · 贴 {def.Komi:0.0} 目 · 人机对战";
		}
		_resumeGameButton.Visible = _resumable;
	}

	// ===================== 联机：面板 =====================
	//
	// 只有一个面板，内容按状态整块重建（比「藏一堆控件再挨个切可见」好维护）。
	// 重建会清掉 LineEdit 里输了一半的地址，所以重建前先存进 _lanIpText、建完再塞回去。

	private void BuildLan()
	{
		_lanPanel = new Control { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
		_lanPanel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.AddChild(_lanPanel);

		var dim = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.76f),
			MouseFilter = MouseFilterEnum.Stop, // 挡住底下的棋盘
		};
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_lanPanel.AddChild(dim);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(0.09f, 0.09f, 0.12f, 0.99f), 32, new Color(1f, 1f, 1f, 0.20f));
		box.ContentMarginLeft = box.ContentMarginRight = 30;
		box.ContentMarginTop = box.ContentMarginBottom = 26;
		panel.AddThemeStyleboxOverride("panel", box);
		dim.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 12);
		panel.AddChild(col);

		var title = new Label { Text = "局域网对战", HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(title, Colors.White, 52, 8);
		col.AddChild(title);

		_lanHead = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		_lanHead.AddThemeFontSizeOverride("font_size", 20);
		_lanHead.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.62f));
		col.AddChild(_lanHead);

		_lanBody = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_lanBody.AddThemeConstantOverride("separation", 10);
		col.AddChild(_lanBody);
	}

	/// <summary>按当前状态重建面板内容。签名没变就直接返回，不碰节点（免得打断输入）。</summary>
	private void RefreshLanPanel(bool force = false)
	{
		string state = _lan == null ? "idle"
			: _lan.Connected ? "live"
			: _lan.IsHost ? "host"
			: _lan.Idle ? "idle"
			: "conn";
		string sig = state;
		if (_lan != null && state == "idle")
			foreach (LanNet.Room r in _lan.Rooms)
				sig += $"|{r.Ip}>{r.Level}:{r.Name}";
		if (!force && sig == _lanSig)
			return;
		_lanSig = sig;

		if (_lanIpInput != null)
			_lanIpText = _lanIpInput.Text;

		foreach (Node c in _lanBody.GetChildren())
		{
			_lanBody.RemoveChild(c);
			c.QueueFree();
		}
		_lanIpInput = null;

		_lanHead.Text = $"你叫「{_myName}」· 本机地址 {LanNet.FirstLanIp()}";

		switch (state)
		{
			case "host":
				BuildLanHosting();
				break;
			case "conn":
				BuildLanConnecting();
				break;
			case "live":
				_lanPanel.Visible = false; // 已经开局了，面板不该还挂着
				break;
			default:
				BuildLanIdle();
				break;
		}
	}

	private void BuildLanIdle()
	{
		Button create = LanButton("创建房间（等对方加入）", new Color("#3ddc97"),
			new Color("#08301f"), 30, 96);
		create.Pressed += CreateRoom;
		_lanBody.AddChild(create);

		_lanBody.AddChild(LanCaption("局域网里的房间（点一下加入）"));

		int shown = 0;
		if (_lan != null)
		{
			foreach (LanNet.Room r in _lan.Rooms)
			{
				if (shown++ >= 3)
					break;
				int lv = Mathf.Clamp(r.Level, 0, Levels.Length - 1);
				string ip = r.Ip;
				Button b = LanButton($"{Short(r.Name, 7)} · {Levels[lv].Size} 路 · {ip}",
					new Color(1f, 1f, 1f, 0.10f), Colors.White, 26, 82);
				b.AddThemeStyleboxOverride("hover",
					GameArt.MakeBox(new Color(1f, 1f, 1f, 0.20f), 22, new Color(1f, 1f, 1f, 0.5f)));
				b.Pressed += () => JoinRoom(ip);
				_lanBody.AddChild(b);
			}
		}
		if (shown == 0)
			_lanBody.AddChild(LanCaption("还没搜到房间 · 让房主先点「创建房间」", dim: true));

		_lanBody.AddChild(LanCaption("或者直接输入对方的地址"));

		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 10);
		_lanBody.AddChild(row);

		_lanIpInput = new LineEdit
		{
			Text = _lanIpText,
			PlaceholderText = "例如 192.168.1.5",
			CustomMinimumSize = new Vector2(372, 78),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		_lanIpInput.AddThemeFontSizeOverride("font_size", 26);
		// 主题里 LineEdit 默认只有一条下划线，放在这个深色面板里几乎看不见。
		// 显式给一个带底色的圆角框，并把上下内边距撑开，字才会在框里居中。
		_lanIpInput.AddThemeStyleboxOverride("normal", IpBox(new Color(1f, 1f, 1f, 0.08f), new Color(1f, 1f, 1f, 0.26f)));
		_lanIpInput.AddThemeStyleboxOverride("focus", IpBox(new Color(1f, 1f, 1f, 0.14f), new Color("#4fa8ff")));
		_lanIpInput.AddThemeColorOverride("font_color", Colors.White);
		_lanIpInput.AddThemeColorOverride("font_placeholder_color", new Color(1f, 1f, 1f, 0.42f));
		_lanIpInput.AddThemeColorOverride("caret_color", Colors.White);
		_lanIpInput.TextSubmitted += _ => JoinTyped();
		row.AddChild(_lanIpInput);

		var join = new Button { Text = "连接", CustomMinimumSize = new Vector2(178, 78) };
		GameArt.StyleButton(join, new Color("#4fa8ff"), Colors.White, radius: 20, fontSize: 28);
		join.Pressed += JoinTyped;
		row.AddChild(join);

		Button back = LanButton("返回", new Color(1f, 1f, 1f, 0.14f), Colors.White, 28, 82);
		back.Pressed += CloseLanPanel;
		_lanBody.AddChild(back);
	}

	private void BuildLanHosting()
	{
		var wait = new Label { Text = "等对方加入…", HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(wait, new Color("#7ee787"), 40, 6);
		_lanBody.AddChild(wait);

		_lanBody.AddChild(LanCaption("让对方在「加入」里输入这个地址"));

		var ip = new Label { Text = LanNet.FirstLanIp(), HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(ip, new Color("#ffd77a"), 52, 8);
		_lanBody.AddChild(ip);

		_lanBody.AddChild(LanCaption("同一个 Wi-Fi · 系统弹「允许访问网络」时要点允许"));

		Button cancel = LanButton("取消房间", new Color("#ff8f6b"), Colors.White, 28, 84);
		cancel.Pressed += () =>
		{
			TeardownLan();
			RefreshLanPanel(true);
		};
		_lanBody.AddChild(cancel);

		Button back = LanButton("返回", new Color(1f, 1f, 1f, 0.14f), Colors.White, 28, 82);
		back.Pressed += CloseLanPanel;
		_lanBody.AddChild(back);
	}

	private void BuildLanConnecting()
	{
		var wait = new Label { Text = "正在连接…", HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(wait, new Color("#8fd3ff"), 38, 6);
		_lanBody.AddChild(wait);

		_lanBody.AddChild(LanCaption("连不上就回上一步，让对方把地址念给你"));

		Button cancel = LanButton("取消", new Color(1f, 1f, 1f, 0.14f), Colors.White, 28, 82);
		cancel.Pressed += () =>
		{
			TeardownLan();
			RefreshLanPanel(true);
		};
		_lanBody.AddChild(cancel);
	}

	private Button LanButton(string text, Color bg, Color ink, int fontSize, float h)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(560, h) };
		GameArt.StyleButton(b, bg, ink, radius: 22, fontSize: fontSize);
		return b;
	}

	/// <summary>地址输入框的底框（圆角 + 内边距）。</summary>
	private static StyleBoxFlat IpBox(Color fill, Color border)
	{
		var box = GameArt.MakeBox(fill, 18, border);
		box.ContentMarginLeft = 20;
		box.ContentMarginRight = 16;
		box.ContentMarginTop = 20;
		box.ContentMarginBottom = 18;
		return box;
	}

	private Label LanCaption(string text, bool dim = false)
	{
		var lb = new Label
		{
			Text = text,
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		lb.AddThemeFontSizeOverride("font_size", 19);
		lb.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, dim ? 0.42f : 0.62f));
		return lb;
	}

	/// <summary>名字太长会把按钮和 HUD 撑爆，按字符数硬截（中文一个字算一个）。</summary>
	private static string Short(string s, int n)
		=> string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n]);

	// ===================== 联机：开关 =====================

	private void OpenLanPanel()
	{
		_myName = _myName.Length > 0 ? _myName : LanNet.RandomName();

		// 进面板要把棋盘「冻住」，但退出面板还得能接着下 —— 先把可继续状态存下来
		if (_phase is Phase.Play or Phase.Scoring)
		{
			_resumeTo = _phase;
			_resumable = _moveCount > 0;
		}
		_phase = Phase.Setup;
		_aiWait = 0f;
		_aiThinking = false;
		_setup.Visible = false;
		_result.Visible = false;

		if (_lan == null)
		{
			var lan = new LanNet { MyName = _myName };
			lan.StartScan(); // 只是听广播，收不到也能手输 IP
			_lan = lan;
			if (lan.LastError.Length > 0)
				ShowToast(lan.LastError);
		}
		else
		{
			_lan.MyName = _myName;
		}

		_lanPanel.Visible = true;
		RefreshLanPanel(true);
		UpdateScoringUi();
		UpdateHud();
		Redraw();
	}

	private void CloseLanPanel()
	{
		_lanPanel.Visible = false;
		TeardownLan();
		RefreshSetup();
		_setup.Visible = true;
		UpdateScoringUi();
		UpdateHud();
		Redraw();
	}

	/// <summary>把占着的端口和连接全放掉。不这么做的话，第二次建房会「端口被占用」。</summary>
	private void TeardownLan()
	{
		_lan?.Close();
		_lan = null;
		_lanLive = false;
		_lanSig = "";
		_lanIpText = "";
		_lanIpInput = null;
	}

	/// <summary>离开联机局，回到「选难度」。</summary>
	private void LeaveLan()
	{
		_lanPanel.Visible = false;
		TeardownLan();
		_result.Visible = false;
		_resumable = false;
		_phase = Phase.Setup;
		RefreshSetup();
		_setup.Visible = true;
		UpdateScoringUi();
		UpdateHud();
		Redraw();
	}

	private void CreateRoom()
	{
		TeardownLan();
		var lan = new LanNet { MyName = _myName };
		if (!lan.StartHost(_myName, _levelIndex))
		{
			ShowToast(lan.LastError);
			lan.Dispose();
			RefreshLanPanel(true);
			return;
		}
		_lan = lan;
		GD.Print($"[Go][lan] 建房 · 昵称「{_myName}」· {LanNet.FirstLanIp()}:{LanNet.GamePort}");
		RefreshLanPanel(true);
	}

	private void JoinTyped()
	{
		string ip = (_lanIpInput?.Text ?? _lanIpText).Trim();
		if (ip.Length == 0)
		{
			ShowToast("先输入对方的地址");
			return;
		}
		JoinRoom(ip);
	}

	private void JoinRoom(string ip)
	{
		if (_lan == null)
			_lan = new LanNet { MyName = _myName };
		_lan.MyName = _myName;
		if (!_lan.Connect(ip))
		{
			ShowToast(_lan.LastError);
			TeardownLan();
		}
		else
		{
			GD.Print($"[Go][lan] 连 {ip}:{LanNet.GamePort}");
		}
		RefreshLanPanel(true);
	}

	// ===================== 联机：事件 =====================

	private void HandleLanEvent(LanNet.Ev ev)
	{
		switch (ev.Kind)
		{
			case LanNet.EvKind.Joined:
				if (_lan == null)
					break;
				if (_lan.IsHost)
				{
					// 房主执黑、并且负责开局：把路数一起发过去，两边同时开
					_myColor = Black;
					_lanLive = true;
					_lan.SendStart(_levelIndex);
					BeginLanGame(_levelIndex);
				}
				else
				{
					_myColor = White;
					GD.Print($"[Go][lan] 连上了，对手「{_lan.PeerName}」");
					UpdateHud();
				}
				break;

			case LanNet.EvKind.Left:
				OnLanPeerLeft();
				break;

			case LanNet.EvKind.Failed:
				ShowToast(ev.Text);
				TeardownLan();
				RefreshLanPanel(true);
				break;

			case LanNet.EvKind.Msg:
				HandleLanMsg(ev);
				break;
		}
	}

	private void HandleLanMsg(LanNet.Ev ev)
	{
		if (_lan == null)
			return;

		switch (ev.Type)
		{
			case "hi":
				UpdateHud(); // 拿到对方昵称，HUD 上换成名字
				break;

			case "start":
				if (_lan.IsHost)
					break; // 房主自己已经开好了
				_myColor = White;
				_lanLive = true;
				BeginLanGame(ev.A);
				break;

			case "move":
				ApplyRemoteMove(ev.A);
				break;

			case "pass":
				ApplyRemotePass();
				break;

			case "undo":
				if (_phase == Phase.Play)
				{
					RollbackPlies(1);
					ShowToast("对方悔了一手");
				}
				break;

			case "resume":
				ResumePlay();
				break;

			case "restart":
				StartGame(_levelIndex);
				ShowToast("再来一局");
				break;

			case "bad":
				ShowToast("这一手不能下（自杀 / 打劫）");
				break;

			case "bye":
				OnLanPeerLeft();
				break;
		}
	}

	private void BeginLanGame(int level)
	{
		StartGame(level);
		_lanPanel.Visible = false;
		_phase = Phase.Play;
		UpdateScoringUi();
		UpdateHud();
		Redraw();
		ShowToast(_lan != null && _lan.IsHost
			? "对手进来了 · 你执黑先下"
			: $"连上了 · 你执白 · 对手 {Short(_lan?.PeerName ?? "对手", 7)}");
	}

	private void ApplyRemoteMove(int p)
	{
		if (_lan == null || !_lanLive || _phase != Phase.Play)
			return;

		// 只认「对方那一手」：轮到我自己时收到落子，说明消息迟到或者对面越权，
		// 一律不收 —— 收了就会把两边的盘面搞岔，之后整局都对不上。
		if (_turn == _myColor)
		{
			GD.Print($"[Go][lan] 拒收越权落子 {Coord(p)}");
			return;
		}

		if (!TryPlay(p, (sbyte)_turn))
		{
			GD.PushError($"[Go][lan] 对方的落子不合规则 {Coord(p)}");
			_lan.SendBad();
		}
	}

	private void ApplyRemotePass()
	{
		if (_lan == null || !_lanLive || _phase != Phase.Play)
			return;
		if (_turn == _myColor)
			return;
		PassMove((sbyte)_turn);
	}

	private void LanMove(int p)
	{
		if (_lan == null)
			return;
		if (!_lan.Connected)
		{
			ShowToast("还没连上对方");
			return;
		}
		if (_phase != Phase.Play)
			return;
		if (_turn != _myColor)
		{
			ShowToast("等对手下完这一手");
			return;
		}
		if (_board[p] != Empty)
		{
			ShowToast("这里已经有子了");
			return;
		}

		if (!_lan.IsHost)
		{
			// 客方不做本地判定：房主说这手行才落子。省掉「两边规则不一致」的一整类麻烦
			_lan.SendMove(p);
			return;
		}

		if (!TryPlay(p, _myColor))
		{
			ShowToast("这一手不能下（自杀 / 打劫）");
			return;
		}
		_lan.SendMove(p);
	}

	private void LanPass()
	{
		if (_lan == null || _phase != Phase.Play)
			return;
		if (_turn != _myColor)
		{
			ShowToast("还没轮到你");
			return;
		}
		_lan.SendPass();
		PassMove(_myColor);
	}

	private void LanUndo()
	{
		if (_lan == null || _phase != Phase.Play || _undo.Count == 0)
		{
			ShowToast("没有可悔的棋");
			return;
		}
		// 悔棋是「各退一手」：双方都把最后一手退掉，谁先按都一样，不需要房主裁决
		_lan.SendUndo();
		RollbackPlies(1);
		ShowToast("悔了一手");
	}

	/// <summary>退 n 手，并把棋盘恢复到「正常下棋」的状态（结算遮罩也要收掉）。</summary>
	private void RollbackPlies(int n)
	{
		while (n-- > 0 && _undo.Count > 0)
			RestoreLast();

		_dead = new bool[_board.Length];
		_owner = new sbyte[_board.Length];
		_phase = Phase.Play;
		_aiWait = 0f;
		_aiThinking = false;
		_result.Visible = false;
		PlaySfx(1.6f, -26f);
		UpdateScoringUi();
		UpdateHud();
		Redraw();
		GD.Print($"[Go] 悔棋 -> moves={_moveCount} turn={(_turn == Black ? "黑" : "白")}");
	}

	private void OnLanPeerLeft()
	{
		bool wasLive = _lanLive;
		TeardownLan();

		if (!wasLive)
		{
			ShowToast("连接断开了");
			RefreshLanPanel(true);
			return;
		}

		_phase = Phase.Done;
		_resultTitle.Text = "对方离开了";
		_resultInfo.Text = "";
		_resultSub.Text = "局域网连接断了 · 想再来一局就重新建房";
		_resultAgain.Visible = false;
		_resultChange.Visible = false;
		_resultQuit.Text = "回首页";
		_result.Visible = true;
		UpdateScoringUi();
		UpdateHud();
		Redraw();
	}

	private void BuildResult()
	{
		_result = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.62f),
			MouseFilter = MouseFilterEnum.Stop,
			Visible = false,
		};
		_result.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.AddChild(_result);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(0.10f, 0.08f, 0.05f, 0.98f), 32, new Color(1f, 1f, 1f, 0.20f));
		box.ContentMarginLeft = box.ContentMarginRight = 38;
		box.ContentMarginTop = box.ContentMarginBottom = 30;
		panel.AddThemeStyleboxOverride("panel", box);
		_result.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 14);
		panel.AddChild(col);

		_resultTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(_resultTitle, Colors.White, 52, 8);
		col.AddChild(_resultTitle);

		_resultInfo = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_resultInfo.AddThemeFontSizeOverride("font_size", 34);
		_resultInfo.AddThemeColorOverride("font_color", new Color("#ffd77a"));
		col.AddChild(_resultInfo);

		// 联机那句说明比人机长，不换行会把整个面板撑出屏幕
		_resultSub = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			CustomMinimumSize = new Vector2(600, 0),
		};
		_resultSub.AddThemeFontSizeOverride("font_size", 24);
		_resultSub.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.70f));
		col.AddChild(_resultSub);

		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		row.AddThemeConstantOverride("separation", 14);
		col.AddChild(row);

		var again = MakeOverlayButton("再来一局", new Color("#ff8f6b"));
		again.Pressed += () =>
		{
			if (_lan != null)
			{
				// 联机是怎么重开：通知对方一起开新局，谁按都行（重复开也只是多清一次盘）
				_lan.SendRestart();
				StartGame(_levelIndex);
				ShowToast("再来一局");
				return;
			}
			if (_puzzle != null)
				StartPuzzle(_puzzleIndex);
			else
				StartGame(_levelIndex);
		};
		row.AddChild(again);
		_resultAgain = again;

		var change = MakeOverlayButton("换难度", new Color("#4fa8ff"));
		change.Pressed += ShowSetup;
		row.AddChild(change);
		_resultChange = change;

		var quit = MakeOverlayButton("回首页", new Color("#8f7bff"));
		quit.Pressed += () =>
		{
			if (_lan != null || _lanLive)
				LeaveLan();          // 联机时这个按钮写的是「离开房间」：先退房间，别把对面挂着
			else
				GetTree().ChangeSceneToFile(ScenePaths.Home);
		};
		row.AddChild(quit);
		_resultQuit = quit;
	}

	private Button MakeOverlayButton(string text, Color bg)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(196, 94) };
		GameArt.StyleButton(b, bg, Colors.White, fontSize: 28);
		return b;
	}

	private void ShowResult(int neutral)
	{
		bool lan = _lan != null;
		bool won = _blackScore > _whiteScore;
		bool draw = Mathf.IsEqualApprox(_blackScore, _whiteScore);
		_resultAgain.Text = _puzzle != null ? "重开本题" : "再来一局";

		if (lan)
		{
			// 联机时黑不是我，就是对手赢
			bool meWin = won == (_myColor == Black);
			_resultTitle.Text = draw ? "平局" : meWin ? "你赢了！" : "对手赢了";
			_resultInfo.Text = $"黑 {_blackScore:0.0}  :  白 {_whiteScore:0.0}";
			_resultSub.Text = $"按盘面数子 · 白含贴 {Levels[_levelIndex].Komi:0.0}" +
							  (neutral > 0 ? $" · 单官 {neutral}" : "") + " · 有争议就再来一局";
			_resultAgain.Visible = true;
			_resultChange.Visible = false; // 联机局不能单方面换难度
			_resultQuit.Text = "离开房间";
		}
		else
		{
			_resultTitle.Text = won ? "你赢了！" : _whiteScore > _blackScore ? "电脑赢了" : "平局";
			_resultInfo.Text = $"黑 {_blackScore:0.0}  :  白 {_whiteScore:0.0}";
			_resultSub.Text = $"白棋含贴目 {Levels[_levelIndex].Komi:0.0} · 黑棋 = 你" +
							  (neutral > 0 ? $" · 单官 {neutral}" : "") + PuzzleResultNote();
			_resultAgain.Visible = true;
			_resultChange.Visible = true;
			_resultQuit.Text = "回首页";
		}

		_result.Visible = true;
	}

	/// <summary>结算时顺带交代残局解没解开：解题和对战两条完成路径都留着。</summary>
	private string PuzzleResultNote()
	{
		if (_puzzle == null)
			return "";
		return _puzzleSolved
			? $" · 残局「{_puzzle.Name}」已过关 ✓ · 这盘棋也下完了"
			: $" · 残局「{_puzzle.Name}」还没解开 · 可以重开本题再试";
	}

	// ===================== 音效 =====================

	private void BuildSfxPool()
	{
		// 音效文件是「有就用、没有就静音」：项目里其实没有 sfx/ding.wav，
		// 但其他游戏都是这么写的，将来补上文件就自动有声音了。
		_hasSfx = ResourceLoader.Exists(SfxPath);
		if (!_hasSfx)
			return;
		var stream = GD.Load<AudioStream>(SfxPath);
		for (int i = 0; i < SfxVoices; i++)
		{
			var p = new AudioStreamPlayer { Stream = stream };
			AddChild(p);
			_sfxPool.Add(p);
		}
	}

	private void PlaySfx(float pitch, float volumeDb)
	{
		if (!_hasSfx || _sfxPool.Count == 0)
			return;
		_sfxIndex = (_sfxIndex + 1) % _sfxPool.Count;
		var p = _sfxPool[_sfxIndex];
		p.PitchScale = pitch;
		p.VolumeDb = volumeDb;
		p.Play();
	}

	// ===================== 内嵌节点 =====================

	/// <summary>
	/// 棋盘的画布。逻辑全在 <see cref="GoGame"/> 里，它只把 _Draw 转回去 ——
	/// 这样「数据 + 规则 + 绘制」在一个文件里是一条线，不用在两边来回跳。
	/// C# 里继承 Godot 节点类型的类必须写 partial，否则报 GD0001。
	/// </summary>
	private sealed partial class BoardView : Node2D
	{
		private readonly GoGame _game;

		public BoardView(GoGame game) => _game = game;

		public override void _Draw() => _game.DrawBoard(this);
	}

	// ===================== 自测 =====================
	//
	// 触发方式：项目根目录放 selftest.flag，内容写 go，然后启动游戏
	// （首页会立刻切到本场景，本场景看到内容是自己就跑这一套）。
	//
	// 注意：自测是 fire-and-forget 协程，异常会被 Task 静静吞掉（表现为日志戛然而止、
	// 进程挂着不退出），所以整个自测体必须包在 try/catch 里。

	private async Task RunSelfTestAsync()
	{
		try
		{
			GD.Print("[SELFTEST] begin (go)");
			await Wait(0.4);

			int fails = 0;
			_seed = 20261107;
			_aiDelayMs = 0;

			// ① 三档：路数、贴目、棋盘尺寸都跟着变，且逐级变大
			bool sizesOk = true;
			for (int i = 0; i < Levels.Length; i++)
			{
				StartGame(i);
				bool ok = _n == Levels[i].Size && _board.Length == _n * _n &&
						  Mathf.IsEqualApprox(Levels[i].Komi, Levels[i].Size switch
						  {
							  9 => 5.5f, 13 => 6.5f, _ => 7.5f,
						  }) &&
						  _cell * (_n - 1) <= StageW - PadX * 2f + 0.5f &&
						  _origin.Y >= BoardTop - 0.5f &&
						  _origin.Y + _cell * (_n - 1) <= BoardBottom + 0.5f;
				GD.Print($"[SELFTEST] level \"{Levels[i].Name}\": n={_n} cell={_cell:0.0} " +
						 $"origin={_origin} komi={Levels[i].Komi:0.0} -> {ok}");
				if (!ok) fails++;
				sizesOk &= ok;
			}
			bool ramp = Levels[0].Size < Levels[1].Size && Levels[1].Size < Levels[2].Size;
			GD.Print($"[SELFTEST] 难度阶梯 9 < 13 < 19 -> {ramp}");
			if (!ramp) fails++;

			// ② 背景真的铺满了吗（露出 0.3 灰的清屏色就说明背景没覆盖整屏）
			var shot = GetViewport().GetTexture().GetImage();
			var px = new Vector2I((int)(shot.GetWidth() * 0.01f), (int)(shot.GetHeight() * 0.55f));
			var col = shot.GetPixel(px.X, px.Y);
			bool bgOk = !GameArt.IsClearColor(col);
			GD.Print($"[SELFTEST] background covers screen: pixel{px}={col} -> {bgOk}");
			if (!bgOk) fails++;

			// ③ 提子：白子只剩一口气，黑下上去要把它提掉，并且记进提子数
			StartGame(0);
			T_Clear();
			T_Set(0, 0, Black);
			T_Set(1, 0, White);
			T_Set(2, 0, Black);
			bool capOk = TryPlay(1 * _n + 1, Black) && _board[1] == Empty && _capBlack == 1;
			GD.Print($"[SELFTEST] capture: 白子被提={_board[1] == Empty} 黑提子数={_capBlack} -> {capOk}");
			if (!capOk) fails++;

			// ④ 自杀禁着：四面都是白子，中间那个点黑棋不能下，盘面不能变
			T_Clear();
			T_Set(0, 1, White);
			T_Set(2, 1, White);
			T_Set(1, 0, White);
			T_Set(1, 2, White);
			var beforeSuicide = (sbyte[])_board.Clone();
			bool suicideRejected = !TryPlay(1 * _n + 1, Black);
			bool boardUnchanged = T_Same(beforeSuicide);
			GD.Print($"[SELFTEST] suicide rejected={suicideRejected} 盘面未变={boardUnchanged}");
			if (!suicideRejected || !boardUnchanged) fails++;

			// ⑤ 打劫：黑提一子之后，白不能立刻提回来（那会复现上一手之前的局面）
			T_Clear();
			T_Set(1, 0, Black);
			T_Set(0, 1, Black);
			T_Set(1, 2, Black);
			T_Set(2, 0, White);
			T_Set(1, 1, White);
			T_Set(2, 2, White);
			T_Set(3, 1, White);
			_prevHash = 0;
			_turn = Black;
			bool capKo = TryPlay(1 * _n + 2, Black);   // 黑下在 (2,1)，提掉 (1,1)
			bool whiteStoneGone = _board[1 * _n + 1] == Empty;
			bool koRejected = !TryPlay(1 * _n + 1, White);
			GD.Print($"[SELFTEST] ko: 黑提子={capKo} 白子已提={whiteStoneGone} 白立即提回被拒={koRejected}");
			if (!capKo || !whiteStoneGone || !koRejected) fails++;
			// 隔一手就可以提回来（打劫不是永久禁着）
			TryPlay(8 * _n + 8, White);
			TryPlay(0, Black);
			bool koAfterOneMove = TryPlay(1 * _n + 1, White);
			GD.Print($"[SELFTEST] ko 隔一手后可提回 -> {koAfterOneMove}");
			if (!koAfterOneMove) fails++;

			// ⑥ 数子：黑在 x=1、白在 x=7 各摆一堵墙。
			//    最左一列是黑空、最右一列是白空，中间五列两边都挨着 → 单官谁也不算。
			//    （只摆一堵墙是不够的：整盘剩下的空都会挨着这堵墙，全被判成黑空。）
			StartGame(0);
			T_Clear();
			for (int y = 0; y < _n; y++)
			{
				T_Set(1, y, Black);
				T_Set(7, y, White);
			}
			ComputeScore(out float bScore, out float wScore, out int neutral);
			bool scoreOk = Mathf.IsEqualApprox(bScore, 18f) &&
						   Mathf.IsEqualApprox(wScore, 18f + Levels[0].Komi) && neutral == 45;
			GD.Print($"[SELFTEST] score: 黑={bScore:0.0}（9子+9空） 白={wScore:0.0}" +
					 $"（9子+9空+贴 {Levels[0].Komi:0.0}） 单官={neutral} -> {scoreOk}");
			if (!scoreOk) fails++;

			// ⑦ 死子：黑空里放一颗白子 —— 白多一子，右列不变，左边那列变成双方都挨着的空；
			//    把它标成死子之后，分数必须回到放子之前。
			T_Set(0, 0, White);
			ComputeScore(out float bAlive, out float wAlive, out _);
			_dead[0] = true;
			ComputeScore(out float bDead, out float wDead, out _);
			bool deadOk = Mathf.IsEqualApprox(wAlive, wScore + 1f) &&
						  Mathf.IsEqualApprox(bAlive, bScore - 9f) &&
						  Mathf.IsEqualApprox(bDead, bScore) &&
						  Mathf.IsEqualApprox(wDead, wScore);
			GD.Print($"[SELFTEST] dead stone: 白 {wAlive:0.0}->{wDead:0.0} 黑 {bAlive:0.0}->{bDead:0.0} " +
					 $"（放子前 黑{bScore:0.0}/白{wScore:0.0}）-> {deadOk}");
			_dead[0] = false;
			if (!deadOk) fails++;

			// ⑧ AI 会提子：白棋（电脑）该提掉这颗只剩一口气的黑子
			StartGame(0);
			T_Clear();
			T_Set(0, 0, White);
			T_Set(1, 0, Black);
			T_Set(2, 0, White);
			T_Set(0, 1, White);
			_turn = White;
			int aiCap = ChooseAiMove(White, out int evalCap);
			bool aiCapOk = aiCap == 1 * _n + 1;   // 唯一一口气在 (1,1)
			GD.Print($"[SELFTEST] ai capture: 选点={Coord(aiCap)} 期望=(1,1) eval={evalCap} -> {aiCapOk}");
			if (!aiCapOk) fails++;

			// ⑨ AI 会救自己：白棋只剩一口气，应该下在那口气上
			StartGame(0);
			T_Clear();
			T_Set(1, 1, White);
			T_Set(0, 1, Black);
			T_Set(1, 0, Black);
			T_Set(1, 2, Black);
			// (2,1) 是白棋唯一一口气
			_turn = White;
			int aiSave = ChooseAiMove(White, out int evalSave);
			bool aiSaveOk = aiSave == 1 * _n + 2;
			GD.Print($"[SELFTEST] ai save: 选点={Coord(aiSave)} 期望=(2,1) eval={evalSave} -> {aiSaveOk}");
			if (!aiSaveOk) fails++;

			// ⑩ AI 不会自填真眼
			StartGame(0);
			T_Clear();
			// 白棋一圈围出 (1,1) 这个真眼
			for (int y = 0; y <= 2; y++)
			for (int x = 0; x <= 2; x++)
				if (!(x == 1 && y == 1))
					T_Set(x, y, White);
			bool eyeDetected = IsTrueEye(_board, 1 * _n + 1, White);
			_turn = White;
			int aiEye = ChooseAiMove(White, out _);
			bool aiEyeOk = eyeDetected && aiEye != 1 * _n + 1;
			GD.Print($"[SELFTEST] ai avoids own eye: 识别真眼={eyeDetected} 选点={Coord(aiEye)} -> {aiEyeOk}");
			if (!aiEyeOk) fails++;

			// ⑪ 盘面已定时 AI 会停一手：白棋围出一整圈，剩下的空全是自己的空
			StartGame(0);
			T_Clear();
			for (int i = 1; i <= 7; i++)
			{
				T_Set(1, i, White);
				T_Set(7, i, White);
				T_Set(i, 1, White);
				T_Set(i, 7, White);
			}
			_turn = White;
			_passes = 0;
			int aiPass = ChooseAiMove(White, out int evalPass);
			bool aiPassOk = aiPass < 0;
			GD.Print($"[SELFTEST] ai passes on settled board: 选点={Coord(aiPass)} eval={evalPass} -> {aiPassOk}");
			if (!aiPassOk) fails++;

			// ⑫ 真实点击落子：走 GUI 命中测试那条链，点完要落一颗黑子，电脑要应一手
			StartGame(0);
			await Wait(0.1);
			int tapP = 4 * _n + 4;
			var tapPos = PointCenter(tapP);
			PushGuiClick(tapPos);
			await Wait(0.15);
			bool tapOk = _board[tapP] == Black;
			GD.Print($"[SELFTEST] gui tap places black stone: 占位={_board[tapP]} -> {tapOk}");
			if (!tapOk) fails++;
			await Wait(0.6); // 等电脑应一手
			int whiteOnBoard = CountColor(White);
			bool aiReplied = whiteOnBoard == 1;
			GD.Print($"[SELFTEST] ai replies after human move: 白子={whiteOnBoard} -> {aiReplied}");
			if (!aiReplied) fails++;

			// 边角的交叉点也要点得中：命中范围是「离交叉点 0.62 格以内」，
			// 棋盘的四个角最容易算错（round 到棋盘外一格就会漏）。
			PushGuiClick(PointCenter(0));
			await Wait(0.15);
			bool cornerOk = _board[0] == Black;
			GD.Print($"[SELFTEST] corner tap (0,0): {_board[0]} -> {cornerOk}");
			if (!cornerOk) fails++;
			SaveShot("go_9x9");

			// ⑬ 悔棋：刚下完、电脑还没应手时，退一手就回到「轮到你」，
			//    刚下的那颗子要收回去（这里验的是角上那手）
			int movesBeforeUndo = _moveCount;
			Undo();
			await Wait(0.1);
			bool undoOk = _board[0] == Empty && _moveCount < movesBeforeUndo && _turn == Black;
			GD.Print($"[SELFTEST] undo: 角点={_board[0]}(0=空) moves {movesBeforeUndo}->{_moveCount} " +
					 $"turn={(_turn == Black ? "黑" : "白")} -> {undoOk}");
			if (!undoOk) fails++;
			// 再退两手应该把中心那颗也收掉（电脑刚下完的情况）
			TryPlay(tapP, Black);
			await Wait(0.6);
			Undo();
			await Wait(0.1);
			bool undo2Ok = _board[tapP] == Empty && _turn == Black;
			GD.Print($"[SELFTEST] undo 2 plies: 中心={_board[tapP]}(0=空) " +
					 $"turn={(_turn == Black ? "黑" : "白")} -> {undo2Ok}");
			if (!undo2Ok) fails++;

			// ⑭ 两次停一手 → 进入数子；结算 → 结果面板
			StartGame(0);
			PassMove(Black);
			await Wait(0.1);
			PassMove(White);
			await Wait(0.1);
			bool scoringOk = _phase == Phase.Scoring && _settleButton.Visible && _passButton.Visible == false;
			GD.Print($"[SELFTEST] two passes -> scoring: phase={_phase} settleBtn={_settleButton.Visible} " +
					 $"passBtn={_passButton.Visible} -> {scoringOk}");
			if (!scoringOk) fails++;
			Settle();
			await Wait(0.15);
			bool settleOk = _phase == Phase.Done && _result.Visible &&
							_resultTitle.Text.Length > 0;
			GD.Print($"[SELFTEST] settle: phase={_phase} result={_result.Visible} " +
					 $"title=\"{_resultTitle.Text}\" 黑={_blackScore:0.0} 白={_whiteScore:0.0} -> {settleOk}");
			if (!settleOk) fails++;
			// 空盘结算：黑 0、白 = 贴目 → 电脑赢（这也是判据的一部分）
			bool emptyScoreOk = Mathf.IsEqualApprox(_blackScore, 0f) &&
								Mathf.IsEqualApprox(_whiteScore, Levels[0].Komi);
			GD.Print($"[SELFTEST] empty board score: 黑={_blackScore:0.0} 白={_whiteScore:0.0} -> {emptyScoreOk}");
			if (!emptyScoreOk) fails++;
			SaveShot("go_result");

			// ⑮ 数子阶段点死子：整块标记，预览分跟着变
			StartGame(0);
			T_Clear();
			for (int y = 1; y < 8; y++)
			for (int x = 1; x < 8; x++)
				T_Set(x, y, White);
			_phase = Phase.Scoring;
			_dead = new bool[_board.Length];
			ComputeOwners();
			UpdateHud();
			int deadBefore = 0;
			for (int i = 0; i < _dead.Length; i++)
				if (_dead[i]) deadBefore++;
			ToggleDeadAt(1 * _n + 1);
			int deadAfter = 0;
			for (int i = 0; i < _dead.Length; i++)
				if (_dead[i]) deadAfter++;
			bool deadMarkOk = deadBefore == 0 && deadAfter == 49;
			GD.Print($"[SELFTEST] mark dead group: {deadBefore} -> {deadAfter}（期望 0 -> 49）-> {deadMarkOk}");
			if (!deadMarkOk) fails++;
			await Wait(0.15); // 等一帧真的画出来，否则截到的是上一帧（还停在结算遮罩上）
			SaveShot("go_scoring");
			ToggleDeadAt(1 * _n + 1); // 取消掉，别把状态留给下一条

			// ⑯ 19 路：棋盘要装得下、星位对、能正常落子
			StartGame(2);
			await Wait(0.1);
			var center19 = PointCenter(9 * _n + 9);
			PushGuiClick(center19);
			await Wait(0.2);
			bool bigOk = _n == 19 && _board[9 * _n + 9] == Black &&
						 _cell >= MinCell && _stars.Length == 9 &&
						 PanelPad * _cell * 2f + _cell * (_n - 1) <= StageW + 0.5f;
			GD.Print($"[SELFTEST] 19 路: n={_n} cell={_cell:0.0} stars={_stars.Length} " +
					 $"天元={_board[9 * _n + 9]} -> {bigOk}");
			if (!bigOk) fails++;

			// ⑰ 19 路上电脑也下一手（顺带量一下耗时：19 路的候选点最多）
			await Wait(0.5);
			int white19 = CountColor(White);
			GD.Print($"[SELFTEST] 19 路 AI 应手: 白子={white19} 用时={_aiDelayMs}ms -> {white19 == 1}");
			if (white19 != 1) fails++;
			SaveShot("go_19x19");

			// ⑱ 中局盘面截图：左右各摆几颗子，看棋子 / 最后一手标记 / 提子光环
			StartGame(1);
			await Wait(0.1);
			RandomPlace(White, 10);
			Redraw();
			await Wait(0.1);
			SaveShot("go_midgame");

			// ⑲ 棋盘真的画在屏幕上：取「四个交叉点正中间」那个像素 ——
			//    它离最近的交叉点有 0.7 格远，任何棋子都盖不到，必然是木色。
			var img = GetViewport().GetTexture().GetImage();
			var bp = _origin + new Vector2(_cell * 0.5f, _cell * 0.5f);
			var bc = img.GetPixel((int)bp.X, (int)bp.Y);
			bool boardPainted = Mathf.Abs(bc.R - bc.G) > 0.06f && bc.R > 0.55f;
			GD.Print($"[SELFTEST] board painted at {bp}: {bc} -> {boardPainted}");
			if (!boardPainted) fails++;

			// ⑳ 难度面板：三档都在，真实点一下「初级」要能开一局 13 路
			ShowSetup();
			await Wait(0.15);
			bool setupOk = _setup.Visible && _levelSubs[1].Text.Contains("13 路") &&
						   _levelSubs[2].Text.Contains("19 路") && !_resumeGameButton.Visible;
			await Wait(0.1);
			SaveShot("go_setup");
			GD.Print($"[SELFTEST] setup panel: visible={_setup.Visible} sub1=\"{_levelSubs[1].Text}\" " +
					 $"sub2=\"{_levelSubs[2].Text}\" -> {setupOk}");
			if (!setupOk) fails++;
			PushGuiClick(_levelButtons[1].GetGlobalRect().GetCenter());
			await Wait(0.2);
			bool pickOk = !_setup.Visible && _n == 13 && _phase == Phase.Play && _moveCount == 0;
			GD.Print($"[SELFTEST] pick level by click: n={_n} phase={_phase} moves={_moveCount} -> {pickOk}");
			if (!pickOk) fails++;

			// ㉑ AI 自战一整盘 9 路：黑白都交给机器人，验证「轮流落子 → 停一手 → 数子」
			//    这条链路不会卡死，也不会走出非法手（顺带压一压 9 路的最坏耗时）。
			StartGame(0);
			int plies = 0;
			for (; plies < 160 && _phase == Phase.Play; plies++)
			{
				sbyte side = _turn == Black ? Black : White;
				int mv = ChooseAiMove(side, out _);
				if (mv < 0)
				{
					PassMove(side);
					continue;
				}
				if (!TryPlay(mv, side))
				{
					GD.Print($"[SELFTEST] 自战出现非法手 {Coord(mv)} side={side}");
					break;
				}
			}
			bool selfPlayOk = (_phase is Phase.Play or Phase.Scoring) && plies > 20 &&
							  CountColor(Black) > 0 && CountColor(White) > 0;
			GD.Print($"[SELFTEST] AI 自战: plies={plies} phase={_phase} " +
					 $"黑={CountColor(Black)} 白={CountColor(White)} -> {selfPlayOk}");
			if (!selfPlayOk) fails++;
			await Wait(0.15);
			SaveShot("go_selfplay");

			// ㉒ 联机面板：打开 → 真实点「创建房间」→ 出现等待界面和地址 → 点「取消房间」
			StartGame(0);
			OpenLanPanel();
			await Wait(0.2);
			bool panelOpenOk = _lanPanel.Visible && _lan != null && _lanBody.GetChildCount() >= 4;
			GD.Print($"[SELFTEST] 联机面板打开: 可见={_lanPanel.Visible} 子控件={_lanBody.GetChildCount()} " +
					 $"-> {panelOpenOk}");
			if (!panelOpenOk) fails++;
			SaveShot("go_lan_panel");

			var createBtn = _lanBody.GetChild(0) as Button;
			if (createBtn == null)
			{
				GD.Print("[SELFTEST] 面板里第一个控件不是按钮，点不到「创建房间」");
				fails++;
			}
			else
			{
				PushGuiClick(createBtn.GetGlobalRect().GetCenter());
				await Wait(0.25);
				bool hostingOk = _lan != null && _lan.IsHost && _lanPanel.Visible && !_lan.Connected;
				GD.Print($"[SELFTEST] 点了「创建房间」: isHost={_lan?.IsHost} 面板={_lanPanel.Visible} " +
						 $"等待中={_lan != null && !_lan.Connected} -> {hostingOk}");
				if (!hostingOk) fails++;
				SaveShot("go_lan_host");

				// 「取消房间」是倒数第二个按钮（最后一个是「返回」）
				var cancelBtn = _lanBody.GetChild(_lanBody.GetChildCount() - 2) as Button;
				if (cancelBtn == null)
				{
					GD.Print("[SELFTEST] 等待界面里没找到「取消房间」按钮");
					fails++;
				}
				else
				{
					PushGuiClick(cancelBtn.GetGlobalRect().GetCenter());
					await Wait(0.2);
					bool cancelOk = _lan == null && _lanPanel.Visible;
					GD.Print($"[SELFTEST] 点了「取消房间」: _lan 已释放={_lan == null} -> {cancelOk}");
					if (!cancelOk) fails++;
				}
			}

			CloseLanPanel();
			await Wait(0.12);
			bool closedOk = !_lanPanel.Visible && _lan == null && _setup.Visible;
			GD.Print($"[SELFTEST] 关闭联机面板: 面板={_lanPanel.Visible} 回到选难度={_setup.Visible} -> {closedOk}");
			if (!closedOk) fails++;

			// ㉓ 广播包：编码 / 解析往返，外加几条「不像我们家的包」要被拒
			bool packOk = LanNet.PackRoom("安静的棋子 123", 1) == "STICKERGO/1|1|安静的棋子 123" &&
						  LanNet.TryParseRoom("STICKERGO/1|2|小明", out string roomName, out int roomLv) &&
						  roomName == "小明" && roomLv == 2 &&
						  !LanNet.TryParseRoom("HELLO|1|x", out _, out _) &&
						  !LanNet.TryParseRoom("STICKERGO/1|9|x", out _, out _) &&
						  !LanNet.TryParseRoom("STICKERGO/1|1|", out _, out _);
			GD.Print($"[SELFTEST] 广播包编解码 -> {packOk}");
			if (!packOk) fails++;

			// ㉓ 真实 socket：房主 + 客方在同一进程里建一对 TCP 连接。
			//    端口用 47891，不占正式对局的 47800，免得和开着的另一个实例打架。
			const int TestPort = 47891;
			var hostNet = new LanNet(TestPort) { MyName = "房主甲" };
			var guestNet = new LanNet(TestPort) { MyName = "客人乙" };
			var hostEvs = new List<LanNet.Ev>();
			var guestEvs = new List<LanNet.Ev>();

			void Pump()
			{
				hostNet.Update(0.02f);
				guestNet.Update(0.02f);
				hostEvs.AddRange(hostNet.Drain());
				guestEvs.AddRange(guestNet.Drain());
			}

			bool hostOpened = hostNet.StartHost("房主甲", 0);
			bool guestCalling = guestNet.Connect("127.0.0.1");
			float waited = 0f;
			while (waited < 3f && !(hostNet.Connected && guestNet.Connected))
			{
				Pump();
				OS.DelayMsec(10);
				waited += 0.02f;
			}
			// 「连上」和「收到对方的 hello」是两件事：握手包还要再转几圈才到得了。
			// 真实运行时每帧都在 Poll，这里必须手动补几圈，否则昵称永远是空的。
			for (int i = 0; i < 8; i++)
			{
				Pump();
				OS.DelayMsec(8);
			}

			bool handshakeOk = hostOpened && guestCalling && hostNet.Connected && guestNet.Connected &&
							   hostNet.PeerName == "客人乙" && guestNet.PeerName == "房主甲" &&
							   hostEvs.Exists(e => e.Kind == LanNet.EvKind.Joined) &&
							   guestEvs.Exists(e => e.Kind == LanNet.EvKind.Joined);
			GD.Print($"[SELFTEST] LAN 握手: host={hostNet.Connected} guest={guestNet.Connected} " +
					 $"房主看到「{hostNet.PeerName}」 客方看到「{guestNet.PeerName}」 -> {handshakeOk}");
			if (!handshakeOk) fails++;

			// ㉔ 广播发现：客方绑 47801 听，房主每 0.8 秒喊一声。
			//    同一台机器上广播会绕回来，所以这里能自收自测（收不到也不致命，还有手输 IP）。
			guestNet.StartScan();
			bool foundRoom = false;
			for (int i = 0; i < 40 && !foundRoom; i++)
			{
				hostNet.Update(0.3f);   // 让房主的广播计时器走快一点
				guestNet.Update(0.02f);
				OS.DelayMsec(20);
				foundRoom = guestNet.Rooms.Count > 0;
			}
			string foundName = foundRoom ? guestNet.Rooms[0].Name : "(无)";
			int foundLv = foundRoom ? guestNet.Rooms[0].Level : -1;
			GD.Print($"[SELFTEST] 广播发现: 收到房间={foundRoom} 名字=\"{foundName}\" 路数={foundLv}");
			if (!foundRoom) fails++;

			hostNet.SendStart(1);
			for (int i = 0; i < 8; i++)
			{
				Pump();
				OS.DelayMsec(8);
			}
			bool startMsgOk = guestEvs.Exists(e => e.Kind == LanNet.EvKind.Msg &&
												   e.Type == "start" && e.A == 1);
			GD.Print($"[SELFTEST] LAN 开局消息（路数 1 = 13 路）-> {startMsgOk}");
			if (!startMsgOk) fails++;

			// ㉔ 端到端：把房主这个 LanNet 挂到游戏上，让 _Process 自己去收包、自己落子
			StartGame(0);            // 9 路
			_lan = hostNet;
			_lanLive = true;
			_myColor = Black;
			hostEvs.Clear();
			guestEvs.Clear();

			int hostP = 2 * 9 + 2;
			LanMove(hostP);          // 房主（黑）本地下一手，同时要发给对方
			bool localMoveOk = _board[hostP] == Black && _turn == White;
			await Wait(0.2);
			Pump();
			bool echoedOk = guestEvs.Exists(e => e.Kind == LanNet.EvKind.Msg &&
												 e.Type == "move" && e.A == hostP);
			GD.Print($"[SELFTEST] LAN 本地落子 {Coord(hostP)}: 棋盘={_board[hostP]} " +
					 $"发出一手={echoedOk} -> {localMoveOk && echoedOk}");
			if (!(localMoveOk && echoedOk)) fails++;

			int guestP = 4 * 9 + 4;
			guestNet.SendMove(guestP); // 客方（白）下在 (4,4)
			await Wait(0.25);
			bool remoteMoveOk = _board[guestP] == White && _turn == Black && _moveCount == 2;
			GD.Print($"[SELFTEST] LAN 对方落子 {Coord(guestP)}: 棋盘={_board[guestP]} " +
					 $"轮到={(_turn == Black ? "黑" : "白")} 手数={_moveCount} -> {remoteMoveOk}");
			if (!remoteMoveOk) fails++;

			// 越权：还没轮到对方，他发来的落子一律不收（收了之后两边盘面就再也对不上）
			int cheatP = 6 * 9 + 6;
			guestNet.SendMove(cheatP);
			await Wait(0.2);
			bool cheatBlocked = _board[cheatP] == Empty && _turn == Black;
			GD.Print($"[SELFTEST] LAN 越权落子被拒 {Coord(cheatP)}: 棋盘={_board[cheatP]} -> {cheatBlocked}");
			if (!cheatBlocked) fails++;

			// ㉕ 双方各停一手：联机局不标死子，直接按盘面结算
			LanPass();               // 房主停一手
			await Wait(0.1);
			guestNet.SendPass();     // 对方也停一手
			await Wait(0.25);
			bool lanSettleOk = _phase == Phase.Done && _result.Visible &&
							   _resultTitle.Text.Length > 0;
			GD.Print($"[SELFTEST] LAN 双停一手结算: phase={_phase} " +
					 $"结算面板={_result.Visible} 标题=\"{_resultTitle.Text}\" -> {lanSettleOk}");
			if (!lanSettleOk) fails++;
			await Wait(0.1);
			SaveShot("go_lan_result");

			// 换到白方（客方）的视角看一眼 HUD：左右两栏和中间那行都要对得上
			_result.Visible = false;
			StartGame(0);
			_myColor = White;
			UpdateScoringUi();
			UpdateHud();
			await Wait(0.12);
			bool clientHudOk = _blackLabel.Text.StartsWith("对手") && _whiteLabel.Text.StartsWith("你") &&
							   _levelLabel.Text.Contains(_lan?.PeerName ?? "?") &&
							   _hintLabel.Text.Contains("你执白");
			GD.Print($"[SELFTEST] 客方 HUD: 左=\"{_blackLabel.Text}\" 右=\"{_whiteLabel.Text}\" " +
					 $"副标题=\"{_levelLabel.Text}\" 提示=\"{_hintLabel.Text}\" -> {clientHudOk}");
			if (!clientHudOk) fails++;
			SaveShot("go_lan_client");
			_myColor = Black;

			// 收尾：把联机状态全拆掉，后面的截图和断言回到人机模式
			_lan = null;
			_lanLive = false;
			_result.Visible = false;
			hostNet.Dispose();
			guestNet.Dispose();
			StartGame(0);

			// ㉖ 残局题库：三种路数齐、一步题和多步题都有、初始局面每块棋都有气、
			//    答案点是空的且落在「电脑不许下」的区域里
			var sizes = new HashSet<int>();
			bool hasOneStep = false, hasMultiStep = false;
			for (int i = 0; i < GoPuzzles.All.Length; i++)
			{
				var def = GoPuzzles.All[i];
				StartPuzzle(i);
				sizes.Add(_n);
				if (def.MaxMoves == 1) hasOneStep = true; else hasMultiStep = true;

				var seen = new bool[_board.Length];
				var stones = new List<int>();
				bool legal = true;
				for (int p = 0; p < _board.Length; p++)
				{
					if (_board[p] == Empty || seen[p])
						continue;
					stones.Clear();
					MarkGroup(_board, p, out _, out int libs, stones);
					foreach (int s in stones)
						seen[s] = true;
					if (libs <= 0)
						legal = false;
				}
				bool keysEmpty = true, aiBanned = true;
				foreach (int k in _puzzleKeys)
				{
					if (_board[k] != Empty) keysEmpty = false;
					if (!_puzzleAiBan[k]) aiBanned = false;
				}
				bool ok = _n == def.Size && _board.Length == _n * _n && legal &&
						  keysEmpty && aiBanned && _puzzleKeys.Length > 0 &&
						  !_puzzleSolved && _turn == Black && _phase == Phase.Play;
				GD.Print($"[SELFTEST] puzzle \"{def.Name}\": {_n}路 legal={legal} " +
						 $"keys={_puzzleKeys.Length} empty={keysEmpty} aiBan={aiBanned} -> {ok}");
				if (!ok) fails++;
			}
			bool spread = sizes.Contains(9) && sizes.Contains(13) && sizes.Contains(19) &&
						  hasOneStep && hasMultiStep;
			GD.Print($"[SELFTEST] puzzle spread: 9/13/19={sizes.Contains(9) && sizes.Contains(13) && sizes.Contains(19)} " +
					 $"一步={hasOneStep} 多步={hasMultiStep} -> {spread}");
			if (!spread) fails++;

			// ㉗ 一步题：一手就过关，提子数记对，答案点电脑不许抢
			StartPuzzle(0);
			int k0 = _puzzleKeys[0];
			bool oneStep = TryPlay(k0, Black) && _puzzleSolved && _capBlack == 1 && _puzzleAiBan[k0];
			GD.Print($"[SELFTEST] puzzle one-step: solved={_puzzleSolved} cap={_capBlack} -> {oneStep}");
			if (!oneStep) fails++;

			// ㉘ 多步提子题：中间让电脑在远处应一手，答案点仍然留着，走完两手才过关
			StartPuzzle(1);
			int k1 = _puzzleKeys[0], k2 = _puzzleKeys[1];
			TryPlay(k1, Black);
			int far = -1;
			for (int p = 0; p < _board.Length; p++)
				if (_board[p] == Empty && !_puzzleAiBan[p]) { far = p; break; }
			if (far >= 0) TryPlay(far, White);
			bool multiMid = !_puzzleSolved && _capBlack == 1 && _board[k2] == Empty;
			TryPlay(k2, Black);
			bool multiOk = multiMid && _puzzleSolved && _capBlack == 2;
			GD.Print($"[SELFTEST] puzzle multi-step: 中途未过={multiMid} solved={_puzzleSolved} " +
					 $"cap={_capBlack} -> {multiOk}");
			if (!multiOk) fails++;

			// ㉙ 多步「占点」题：只占一个要点不算，两个都占住才过关
			int twoKey = Array.FindIndex(GoPuzzles.All,
				d => d.Kind == PuzzleGoal.KeyPoints && d.MaxMoves > 1);
			StartPuzzle(twoKey);
			TryPlay(_puzzleKeys[0], Black);
			bool notYet = !_puzzleSolved;
			foreach (int k in _puzzleKeys)
				if (_board[k] == Empty)
					TryPlay(k, Black);
			bool twoKeyOk = twoKey >= 0 && notYet && _puzzleSolved;
			GD.Print($"[SELFTEST] puzzle key-points: 先占一个不算={notYet} 全占住={_puzzleSolved} -> {twoKeyOk}");
			if (!twoKeyOk) fails++;

			// ㉚ 残局也能一路对战到数子：解完题不中断，结算里还记着「已过关」
			StartPuzzle(0);
			TryPlay(_puzzleKeys[0], Black);
			PassMove(White);
			PassMove(Black);
			bool battleOk = _phase == Phase.Scoring && _puzzleSolved;
			Settle();
			bool resultMentionsPuzzle = _resultSub.Text.Contains("已过关");
			battleOk &= _phase == Phase.Done && resultMentionsPuzzle;
			GD.Print($"[SELFTEST] puzzle played out: phase={_phase} 结算提到过关={resultMentionsPuzzle} -> {battleOk}");
			if (!battleOk) fails++;
			_result.Visible = false;

			// ㉛ 续玩：快照 → 重开 → 恢复，盘面 / 残局 / 手数 / 提子数 / 过关标记都要回来
			StartPuzzle(2);
			TryPlay(_puzzleKeys[0], Black);
			var snap = Snapshot();
			var expected = (sbyte[])_board.Clone();
			int expMoves = _moveCount, expCap = _capBlack, expPuzzle = _puzzleIndex;
			bool expSolved = _puzzleSolved;
			StartGame(0);
			Restore(snap);
			bool resumeOk = T_Same(expected) && _puzzleIndex == expPuzzle && _puzzle != null &&
							_moveCount == expMoves && _capBlack == expCap &&
							_puzzleSolved == expSolved && _phase == Phase.Play && !_setup.Visible;
			GD.Print($"[SELFTEST] resume round-trip: puzzle={_puzzleIndex} moves={_moveCount} " +
					 $"cap={_capBlack} solved={_puzzleSolved} boardSame={T_Same(expected)} -> {resumeOk}");
			if (!resumeOk) fails++;

			// 普通对局的快照同样能回来，而且不会误带残局状态
			StartGame(1);
			TryPlay(6 * _n + 6, Black);
			var snap2 = Snapshot();
			var expected2 = (sbyte[])_board.Clone();
			StartGame(0);
			Restore(snap2);
			bool resume2Ok = _n == 13 && _puzzle == null && T_Same(expected2) && _moveCount == 1;
			GD.Print($"[SELFTEST] resume normal game: n={_n} puzzle={_puzzle?.Name ?? "none"} " +
					 $"boardSame={T_Same(expected2)} -> {resume2Ok}");
			if (!resume2Ok) fails++;

			// 已结算的局不该写盘
			_phase = Phase.Done;
			bool noSaveOk = Snapshot().Count == 0 && this is IResumable;
			GD.Print($"[SELFTEST] done game snapshot empty -> {noSaveOk}");
			if (!noSaveOk) fails++;

			// 设置面板里每道残局都有按钮，点得到、开得出题
			ShowSetup();
			bool buttonsOk = _puzzleButtons.Length == GoPuzzles.All.Length &&
							 _puzzleButtons[0].GetChildCount() > 0 &&
							 _puzzleButtons[0].GetChild(0) is VBoxContainer;
			GD.Print($"[SELFTEST] puzzle buttons: count={_puzzleButtons.Length} -> {buttonsOk}");
			if (!buttonsOk) fails++;

			// 面板按钮真的能开题（用信号触发，避免滚动区外的按钮点不到）
			ShowSetup();
			_puzzleButtons[0].EmitSignal(BaseButton.SignalName.Pressed);
			bool btnOk = !_setup.Visible && _puzzleIndex == 0 && _n == 9 && _phase == Phase.Play;
			GD.Print($"[SELFTEST] puzzle button starts game: puzzle={_puzzleIndex} n={_n} -> {btnOk}");
			if (!btnOk) fails++;

			// 留两张图：19 路残局的引导框，以及过关后转绿的同一块区域
			StartPuzzle(GoPuzzles.All.Length - 1);
			await Wait(0.15);
			SaveShot("go_puzzle_19x19");
			foreach (int k in _puzzleKeys)
				if (_board[k] == Empty)
					TryPlay(k, Black);
			await Wait(0.15);
			SaveShot("go_puzzle_solved");

			GD.Print(fails == 0 ? "[SELFTEST] PASSED" : $"[SELFTEST] FAILED ({fails} 项)");
			await Wait(0.4);
			GetTree().Quit();
		}
		catch (Exception e)
		{
			GD.Print($"[SELFTEST] CRASHED: {e.GetType().Name}: {e.Message}");
			GD.Print(e.StackTrace ?? "");
			await Wait(0.2);
			GetTree().Quit();
		}
	}

	// ---------- 自测用的小工具 ----------

	/// <summary>把棋盘清空，回到「黑棋该下」的干净状态。故意绕过规则，方便摆局面。</summary>
	private void T_Clear()
	{
		Array.Clear(_board);
		Array.Clear(_dead);
		Array.Clear(_owner);
		_turn = Black;
		_passes = 0;
		_capBlack = 0;
		_capWhite = 0;
		_prevHash = 0;
		_lastMove = -1;
		_moveCount = 0;
		_undo.Clear();
		_phase = Phase.Play;
		_aiWait = 0f;
		_aiThinking = false;
		_dead = new bool[_board.Length];
		_owner = new sbyte[_board.Length];
		if (_setup != null)
			_setup.Visible = false;
		if (_result != null)
			_result.Visible = false;
		UpdateScoringUi();
	}

	private void T_Set(int x, int y, sbyte color) => _board[y * _n + x] = color;

	private bool T_Same(sbyte[] other)
	{
		if (other.Length != _board.Length)
			return false;
		for (int i = 0; i < _board.Length; i++)
			if (_board[i] != other[i])
				return false;
		return true;
	}

	private int CountColor(sbyte color)
	{
		int c = 0;
		foreach (sbyte v in _board)
			if (v == color)
				c++;
		return c;
	}

	/// <summary>随便摆几颗子，只为截图好看（不关心合法性）。</summary>
	private void RandomPlace(sbyte color, int count)
	{
		var rng = new Random(7);
		for (int i = 0; i < count; i++)
		{
			int p = rng.Next(_board.Length);
			if (_board[p] == Empty)
				_board[p] = color;
		}
	}

	/// <summary>
	/// 推一次「真·点击」给视口：按下 + 抬手，连引擎的模拟触摸一起推，
	/// 和开着 mouse→touch 的真实运行时收到的序列一致（真鼠标 device=0 + 模拟触摸 device=-1）。
	/// 走这条路的点击会先经过 GUI 命中测试，能测出「事件根本没送到棋盘」的问题。
	/// </summary>
	private void PushGuiClick(Vector2 pos)
	{
		var vp = GetViewport();
		vp.PushInput(new InputEventMouseButton
		{
			Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left,
			Pressed = true, Device = 0,
		}, true);
		vp.PushInput(new InputEventScreenTouch { Position = pos, Pressed = true, Device = -1 }, true);
		vp.PushInput(new InputEventMouseButton
		{
			Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left,
			Pressed = false, Device = 0,
		}, true);
		vp.PushInput(new InputEventScreenTouch { Position = pos, Pressed = false, Device = -1 }, true);
	}

	private string SaveShot(string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		string path = $"user://screenshots/{tag}_{stamp}.png";
		var err = img.SavePng(path);
		GD.Print($"[Go] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}
}
