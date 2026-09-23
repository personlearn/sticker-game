#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 扫雷 —— 经典排雷（首页卡片里的「逻辑 · 排雷」）。
///
/// 和前三个游戏比，它是**纯回合制**的：没有一个逐帧推进的世界，只有
/// 「点一下 → 改数据 → 重画棋盘」。所以这里没有像雷霆战机那样的 UpdateLoop，
/// <c>_Process</c> 只负责计时、长按判定和几段很短的动画；没有动画时一帧都不重画。
///
/// 结构上继续沿用全项目的约定：一个脚本 + 一个瘦场景。
/// 场景里只有 Stage / Background / Board / UI / TopBar 这个骨架，
/// 棋盘、HUD、难度面板、结算面板全部由代码创建；
/// 所有图形都是 _Draw 现画的（<see cref="GameArt"/>），不依赖任何素材文件。
///
/// 三档难度只有「行数 / 列数 / 雷数」三个数字不一样（见 <see cref="Levels"/>），
/// 所以「难度越高，行列越多、炸弹也越多」是**数据**决定的，代码里没有一处按难度分叉。
/// </summary>
public partial class MinesweeperGame : Control
{
	// ===================== 可调参数 =====================

	private const float PadX = 26f;          // 棋盘左右留白
	private const float BoardTop = 220f;     // 棋盘可用区上边界（720×1280 逻辑分辨率下的绝对值）
	private const float BoardBottom = 1130f; // 棋盘可用区下边界（下面留给「插旗模式」按钮）
	private const float MinTile = 22f;       // 格子边长下限：再小就点不中了
	private const float MaxTile = 76f;       // 格子边长上限：再大就显得空
	private const float LongPressSeconds = 0.42f; // 按住多久算「长按插旗」
	private const float DragCancelPx = 22f;       // 移动超过这么多像素就取消这次按压

	private const float PopLife = 0.20f;     // 挖开时那一下白闪的时长
	private const float RingLife = 0.55f;    // 爆炸 / 胜利光环的时长
	private const float ShakeDecay = 30f;    // 爆炸震屏的衰减速度（px/s）

	private const string BestPath = "user://minesweeper_best.cfg";
	private const string SfxPath = "res://sfx/ding.wav";
	private const int SfxVoices = 4;

	// ===================== 难度表 =====================

	/// <summary>一档难度。加一档只要往 <see cref="Levels"/> 里加一条。</summary>
	private sealed class LevelDef
	{
		public string Name = "";   // 面板上显示的名字
		public string Tag = "";    // 存档里的 key（改了就丢纪录，别乱改）
		public int Rows;
		public int Cols;
		public int Mines;
		public Color Tint = Colors.White; // 面板上这条的强调色
	}

	/// <summary>
	/// 三档难度。雷的密度也是递增的（12% → 18% → 24%）：
	/// 光加格子不加密度的话，「困难」只是更费手，不是更难。
	/// 15×15 是这个竖屏尺寸下能把格子留在 44px 左右的最大值——再往上加列，
	/// 格子就掉到 40px 以下，手指点不准反而是折磨。
	/// </summary>
	private static readonly LevelDef[] Levels =
	{
		new LevelDef { Name = "简单", Tag = "easy", Rows = 9, Cols = 9, Mines = 10, Tint = new Color("#7ee787") },
		new LevelDef { Name = "普通", Tag = "normal", Rows = 12, Cols = 12, Mines = 26, Tint = new Color("#8fd3ff") },
		new LevelDef { Name = "困难", Tag = "hard", Rows = 15, Cols = 15, Mines = 55, Tint = new Color("#ff8f9c") },
	};

	// ===================== 棋盘数据 =====================

	private enum Phase { Setup, Play, Win, Lose }
	private enum Mark { None = 0, Flag = 1, Question = 2 }

	/// <summary>一格。用 struct 存成数组，省掉 225 次对象分配。</summary>
	private struct Cell
	{
		public bool Mine;
		public int Adj;      // 周围 8 格的雷数
		public bool Open;    // 已挖开
		public Mark Mark;
	}

	private Cell[] _cells = System.Array.Empty<Cell>();
	private int _rows, _cols, _mines, _total;
	private int _levelIndex;
	private Phase _phase = Phase.Setup;

	private bool _seeded;      // 是否已经布雷（第一次点击之后才布，这样首点必安全）
	private int _opened;       // 已挖开的格数（判胜用）
	private int _flags;        // 已插旗数
	private int _wrongFlags;   // 输的时候「插错位置」的旗子数（给自测看）
	private int _boomIndex = -1;
	private bool _flagMode;    // 插旗模式（手机上没有右键，得给个开关）
	private double _elapsed;   // 本局用时（第一次挖开之后才开始走）
	private bool _started;

	private int _seed;         // 0 = 按时间随机；自测里固定成一个值好复现
	private System.Random _rng = new();

	private readonly int[] _best = new int[Levels.Length]; // 各难度最好成绩（秒），0 = 还没通关过

	// ===================== 运行时状态（动画 / 输入） =====================

	private readonly List<Pop> _pops = new();
	private readonly List<Ring> _rings = new();
	private float _shake;
	private double _now;       // 自己累计的时间（长按判定用，比 Godot 的 tick 好测）

	private int _pressIndex = -1;
	private double _pressAt;
	private Vector2 _pressPos;
	private bool _longFired;
	private int _hoverIndex = -1;

	private readonly List<int> _nbBuf = new();   // 邻域查询的复用缓冲（注意别嵌套使用）
	private readonly List<AudioStreamPlayer> _sfxPool = new();
	private int _sfxIndex;

	// ===================== 子节点 =====================

	private Control _stage = null!;
	private TextureRect _background = null!;
	private Node2D _board = null!;
	private Control _ui = null!;
	private HBoxContainer _topBar = null!;
	private Button _homeButton = null!;
	private Button _setupButton = null!;
	private Button _restartButton = null!;

	private GridView _grid = null!;
	private Font? _font;

	private HBoxContainer _hud = null!;
	private Label _mineLabel = null!;
	private Label _timeLabel = null!;
	private Label _bestLabel = null!;
	private Label _levelLabel = null!;
	private Button _flagButton = null!;
	private Label _hintLabel = null!;

	private Control _setup = null!;
	private readonly Button[] _levelButtons = new Button[Levels.Length];
	private readonly Label[] _levelSubs = new Label[Levels.Length];
	private readonly Label[] _levelBests = new Label[Levels.Length];
	private Button _resumeButton = null!;
	private bool _resumable;

	private ColorRect _result = null!;
	private Label _resultTitle = null!;
	private Label _resultInfo = null!;
	private Label _resultNew = null!;
	private Button _againButton = null!;

	// HUD 文本缓存：只在数值真的变了才重建字符串
	private int _hudSecs = -1, _hudMines = int.MinValue, _hudBest = int.MinValue;

	// 画棋盘用的样式盒（缓存起来，_Draw 里不能每格 new 一个）
	private StyleBoxFlat _sbPanel = null!;
	private StyleBoxFlat _sbEdge = null!;
	private StyleBoxFlat _sbFace = null!;
	private StyleBoxFlat _sbFaceHover = null!;
	private StyleBoxFlat _sbOpen = null!;
	private StyleBoxFlat _sbOpenMine = null!;
	private StyleBoxFlat _sbBoom = null!;
	private StyleBoxFlat _sbPop = null!;

	private float _tile = 44f;
	private Vector2 _origin;

	private float StageW => Size.X > 0f ? Size.X : 720f;
	private float StageH => Size.Y > 0f ? Size.Y : 1280f;

	/// <summary>挖开时的一下白闪。</summary>
	private sealed class Pop { public int Index; public float Age; }
	/// <summary>爆炸 / 胜利的扩散光环。</summary>
	private sealed class Ring
	{
		public Vector2 Pos;
		public float Age;
		public Color Tint = new Color("#ff6a7f");
		public float MaxR = 90f;
	}

	// ===================== 生命周期 =====================

	public override void _Ready()
	{
		_stage = GetNode<Control>("Stage");
		_background = GetNode<TextureRect>("Stage/Background");
		_board = GetNode<Node2D>("Stage/Board");
		_ui = GetNode<Control>("UI");
		_topBar = GetNode<HBoxContainer>("UI/TopBar");
		_homeButton = GetNode<Button>("UI/TopBar/HomeButton");
		_setupButton = GetNode<Button>("UI/TopBar/SetupButton");
		_restartButton = GetNode<Button>("UI/TopBar/RestartButton");

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		// 根节点铺满全屏，默认的 MouseFilter = Stop 会「认领」所有落在空白处的点击
		// （UI 面板之外的区域），把事件从引擎的 GUI 流程里截走。棋盘不是 Control、
		// 靠代码自己接事件，被截走就等于点不动 —— 所以根节点必须让路。
		MouseFilter = MouseFilterEnum.Ignore;
		Theme = GameArt.MakeUiTheme();
		_font = GameArt.UiFont; // _Draw 里画数字要用，Theme 里那份拿不到

		// 背景：深夜蓝紫渐变，和首页一个调子
		_background.Texture = GameArt.VerticalGradient(new Color("#0b1022"), new Color("#2a1f45"));
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
		_background.MouseFilter = MouseFilterEnum.Ignore;

		// 棋盘自己画自己：所有格子都在这一个节点的 _Draw 里出来。
		// 注意 ZIndex 保持 0 —— UI 是 Stage 的兄弟节点，两边 z 都是 0 时按树序画，
		// 棋盘一旦给个正数就会盖住顶栏按钮和结算遮罩。
		_grid = new GridView(this);
		_board.AddChild(_grid);

		BuildStyleBoxes();
		BuildSfxPool();
		BuildTopBar();
		BuildHud();
		BuildFlagButton();
		BuildSetup();
		BuildResult();
		LoadBest();

		Layout();
		GetViewport().SizeChanged += Layout;

		GD.Print($"[Mines] ready. best={DescribeBest()} selftest={SelftestFlag.Describe()}");

		if (SelftestFlag.Read() == SelftestFlag.TokenMines)
			_ = RunSelfTestAsync();
		else
			ShowSetup();
	}

	private void Layout()
	{
		// Control 不会自动铺满父节点：尺寸是 0 的话里面的东西全塌缩成一列看不见的
		_stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_stage.MouseFilter = MouseFilterEnum.Ignore;
		_ui.MouseFilter = MouseFilterEnum.Ignore;

		// 背景必须显式铺满 + 关掉「最小尺寸 = 纹理尺寸」：
		// TextureRect 的 min size 跟着纹理走，而渐变纹理只有 8×256，
		// 不铺满就只在左上角画一小块，其余全是视口清屏色（0.3 灰）。
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_background.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;

		ComputeGrid();
	}

	/// <summary>
	/// 按当前行列数算格子边长和棋盘原点。
	/// 边长取「宽高两个方向都放得下」的那个较小值 —— 这样任何一种行列组合
	/// 都是**整体居中**的，不需要给每一档难度手写坐标。
	/// </summary>
	private void ComputeGrid()
	{
		if (_cols <= 0 || _rows <= 0)
		{
			_grid.Position = Vector2.Zero;
			return;
		}

		float availW = StageW - PadX * 2f;
		float availH = (StageH - (StageH - BoardBottom)) - BoardTop;
		float tile = Mathf.Min(availW / _cols, availH / _rows);
		tile = Mathf.Clamp(Mathf.Floor(tile), MinTile, MaxTile);
		_tile = tile;

		_origin = new Vector2(
			(StageW - tile * _cols) * 0.5f,
			BoardTop + (availH - tile * _rows) * 0.5f);
		_grid.Position = _origin;
	}

	private void BuildStyleBoxes()
	{
		// 底板：比背景更暗一点，把棋盘从全屏渐变里「托」出来
		_sbPanel = GameArt.MakeBox(new Color(0.04f, 0.06f, 0.13f, 0.72f), 24, new Color(1f, 1f, 1f, 0.13f));
		// 未挖开的格子用两层盒子叠出「凸起」：底下深色边 + 上面浅色面
		_sbEdge = GameArt.MakeBox(new Color("#141b33"), 9);
		_sbFace = GameArt.MakeBox(new Color("#4a5c94"), 8);
		_sbFaceHover = GameArt.MakeBox(new Color("#5d72ad"), 8);
		// 挖开后是「凹下去」的：一层比底板还暗的平地
		_sbOpen = GameArt.MakeBox(new Color("#151d38"), 8);
		_sbOpenMine = GameArt.MakeBox(new Color("#4a1526"), 8);
		_sbBoom = GameArt.MakeBox(new Color("#c23a52"), 8, new Color("#ffd0d8"));
		_sbPop = GameArt.MakeBox(new Color(1f, 1f, 1f, 0.30f), 8);
	}

	// ===================== 一局的开始与结束 =====================

	private void StartGame(int levelIndex)
	{
		var def = Levels[Mathf.Clamp(levelIndex, 0, Levels.Length - 1)];
		_levelIndex = Mathf.Clamp(levelIndex, 0, Levels.Length - 1);

		_rows = def.Rows;
		_cols = def.Cols;
		_mines = def.Mines;
		_total = _rows * _cols;
		_cells = new Cell[_total];
		// 注意别写 Environment.TickCount：这个文件里有 using Godot，
		// 那个名字会解析到 Godot.Environment（3D 环境资源）上去，编译直接报错。
		_rng = new System.Random(_seed != 0 ? _seed : (int)(Time.GetTicksMsec() & 0x7FFFFFFF));

		_seeded = false;
		_opened = 0;
		_flags = 0;
		_wrongFlags = 0;
		_boomIndex = -1;
		_elapsed = 0;
		_started = false;
		_shake = 0f;
		_hoverIndex = -1;
		CancelPress();
		_pops.Clear();
		_rings.Clear();
		_board.Position = Vector2.Zero;

		_phase = Phase.Play;
		_setup.Visible = false;
		_result.Visible = false;

		ComputeGrid();
		_hudSecs = -1;
		_hudMines = int.MinValue;
		_hudBest = int.MinValue;
		UpdateHud();
		Redraw();

		GD.Print($"[Mines] start \"{def.Name}\" {_rows}×{_cols} · {_mines} 雷");
	}

	/// <summary>
	/// 布雷。**第一次点击的位置及其 8 邻域一定不放雷**：
	/// 这是现代扫雷的通行做法 —— 第一下就炸会让人觉得「游戏在耍我」，
	/// 而保证首点是个空白格，开局直接连片展开，手感也更好。
	/// </summary>
	private void PlaceMines(int safeIndex)
	{
		var forbidden = new HashSet<int> { safeIndex };
		FillNeighbors(safeIndex, _nbBuf);
		foreach (int n in _nbBuf)
			forbidden.Add(n);

		// 极端情况兜底：可选位置不够放雷时，只保护首点本身（宁可开局可能要猜，也不能少放雷）
		if (_total - forbidden.Count < _mines)
		{
			forbidden.Clear();
			forbidden.Add(safeIndex);
		}

		var pool = new List<int>();
		for (int i = 0; i < _total; i++)
			if (!forbidden.Contains(i))
				pool.Add(i);

		// Fisher–Yates 洗牌后取前 _mines 个当雷
		for (int i = pool.Count - 1; i > 0; i--)
		{
			int j = _rng.Next(i + 1);
			(pool[i], pool[j]) = (pool[j], pool[i]);
		}
		for (int i = 0; i < _mines; i++)
			_cells[pool[i]].Mine = true;

		// 顺手把每格的相邻雷数算出来
		for (int i = 0; i < _total; i++)
		{
			if (_cells[i].Mine)
				continue;
			int n = 0;
			FillNeighbors(i, _nbBuf);
			foreach (int k in _nbBuf)
				if (_cells[k].Mine)
					n++;
			_cells[i].Adj = n;
		}
	}

	private void Win()
	{
		_phase = Phase.Win;
		CancelPress();

		// 把剩下的雷全部插上旗：盘面「干净」了，看起来才像赢
		for (int i = 0; i < _total; i++)
			if (_cells[i].Mine && _cells[i].Mark != Mark.Flag)
			{
				_cells[i].Mark = Mark.Flag;
				_flags++;
			}

		int secs = Mathf.Max(1, (int)_elapsed); // 至少记 1 秒，免得出现「00:00 通关」
		bool record = _best[_levelIndex] == 0 || secs < _best[_levelIndex];
		if (record)
		{
			_best[_levelIndex] = secs;
			SaveBest();
		}

		// 每颗雷上放一圈金环，庆祝一下
		for (int i = 0; i < _total; i++)
			if (_cells[i].Mine)
				_rings.Add(new Ring { Pos = CellCenter(i), Tint = new Color("#ffd77a"), MaxR = _tile * 1.6f });

		PlaySfx(1.45f, -10f);
		ShowResult("排雷成功！", $"用时 {FormatTime(secs)} · 最好 {FormatTime(_best[_levelIndex])}",
			record ? "★ 新纪录！" : "");
		UpdateHud();
		Redraw();
		GD.Print($"[Mines] win. level={Levels[_levelIndex].Tag} time={secs}s record={record}");
	}

	/// <summary>踩雷。把整盘雷翻开、给插错的旗子打叉，然后结算。</summary>
	private void Lose(int boomIndex)
	{
		_phase = Phase.Lose;
		_boomIndex = boomIndex;
		CancelPress();

		for (int i = 0; i < _total; i++)
		{
			if (_cells[i].Mine)
				_cells[i].Open = true;
			if (_cells[i].Mark == Mark.Flag && !_cells[i].Mine)
				_wrongFlags++;
		}

		_shake = 20f;
		_rings.Add(new Ring { Pos = CellCenter(boomIndex), Tint = new Color("#ff6a7f"), MaxR = _tile * 3.4f });
		PlaySfx(0.42f, -6f);

		ShowResult("踩到雷了", $"坚持了 {FormatTime(Mathf.Max(1, (int)_elapsed))} · 点「再来一局」重开",
			_wrongFlags > 0 ? $"有 {_wrongFlags} 面旗插错了" : "");
		Redraw();
		GD.Print($"[Mines] lose. boom={boomIndex} wrongFlags={_wrongFlags} opened={_opened}/{_total - _mines}");
	}

	private void ShowResult(string title, string info, string extra)
	{
		_resultTitle.Text = title;
		_resultInfo.Text = info;
		_resultNew.Text = extra;
		_resultNew.Visible = extra.Length > 0;
		_result.Visible = true;
	}

	// ===================== 主循环 =====================

	/// <summary>
	/// 只干三件事：推进计时、判定长按、推进动画。
	/// 没有动画时不会调 QueueRedraw —— 扫雷盘面静止时一帧绘制都不产生。
	/// </summary>
	public override void _Process(double delta)
	{
		_now += delta;

		bool anim = false;

		if (_phase == Phase.Play)
		{
			UpdateLongPress();
			if (_started)
				_elapsed += delta;
			UpdateHud();
		}

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

		if (anim)
			Redraw();
	}

	// ===================== 操作 =====================

	/// <summary>
	/// 主操作：挖开 / 在插旗模式下插旗 / 点已挖开的数字「连开一圈」。
	/// 从输入层剥出来是为了两件事：自测可以直接调它，逻辑也只有一份。
	/// </summary>
	private void PrimaryAction(int idx)
	{
		if (_phase != Phase.Play || idx < 0 || idx >= _total)
			return;

		if (_cells[idx].Open)
		{
			Chord(idx);
			return;
		}
		if (_flagMode)
		{
			ToggleMark(idx);
			return;
		}
		// 插了旗的格子不响应挖开：旗子是玩家自己下的判断，别让手抖一下就给掀了
		if (_cells[idx].Mark == Mark.Flag)
			return;

		Reveal(idx);
	}

	private void Reveal(int idx)
	{
		if (!_seeded)
		{
			PlaceMines(idx);
			_seeded = true;
			_started = true;
		}

		if (_cells[idx].Mine)
		{
			Lose(idx);
			return;
		}

		FloodOpen(idx);
		PlaySfx(1.9f, -24f);
		CheckWin();
		UpdateHud();
		Redraw();
	}

	/// <summary>
	/// 从 idx 开始连片展开：0 的格子把邻居继续往外推。
	/// 两个经典规则照抄：**插了旗的格子不会被自动翻开**（否则旗子毫无意义），
	/// 数字格只是自己开，不再往外扩散。
	/// </summary>
	private void FloodOpen(int start)
	{
		var stack = new Stack<int>();
		stack.Push(start);
		while (stack.Count > 0)
		{
			int i = stack.Pop();
			if (_cells[i].Open || _cells[i].Mine)
				continue;
			if (i != start && _cells[i].Mark == Mark.Flag)
				continue;

			_cells[i].Open = true;
			_cells[i].Mark = Mark.None; // 挖开了，问号自然作废
			_opened++;
			_pops.Add(new Pop { Index = i });

			if (_cells[i].Adj != 0)
				continue;

			FillNeighbors(i, _nbBuf);
			foreach (int n in _nbBuf)
				if (!_cells[n].Open)
					stack.Push(n);
		}
	}

	/// <summary>
	/// 数字格「连开」：周围旗子数 == 数字时，把剩下没插旗的邻居一次全翻开（经典 chord）。
	/// 旗子插错就会翻到雷上——这是玩家自己的判断失误，规则就该这样。
	/// </summary>
	private void Chord(int idx)
	{
		int adj = _cells[idx].Adj;
		if (adj <= 0)
			return;

		FillNeighbors(idx, _nbBuf);
		// 拷一份再动：下面的 Reveal 会递归用 _nbBuf
		var around = new List<int>(_nbBuf);
		int flagged = 0;
		foreach (int n in around)
			if (_cells[n].Mark == Mark.Flag)
				flagged++;
		if (flagged != adj)
			return;

		foreach (int n in around)
		{
			if (_cells[n].Open || _cells[n].Mark == Mark.Flag)
				continue;
			if (_cells[n].Mine)
			{
				Lose(n);
				return;
			}
			FloodOpen(n);
		}
		PlaySfx(1.6f, -20f);
		CheckWin();
		UpdateHud();
		Redraw();
	}

	/// <summary>三态循环：无 → 旗 → 问号 → 无（和经典扫雷右键一致，问号用来「这里我还没想好」）。</summary>
	private void ToggleMark(int idx)
	{
		if (_phase != Phase.Play || _cells[idx].Open)
			return;

		switch (_cells[idx].Mark)
		{
			case Mark.None:
				_cells[idx].Mark = Mark.Flag;
				_flags++;
				PlaySfx(2.1f, -22f);
				break;
			case Mark.Flag:
				_cells[idx].Mark = Mark.Question;
				_flags--;
				PlaySfx(1.5f, -26f);
				break;
			default:
				_cells[idx].Mark = Mark.None;
				break;
		}
		UpdateHud();
		Redraw();
	}

	private void CheckWin()
	{
		if (_phase == Phase.Play && _opened >= _total - _mines)
			Win();
	}

	private void ToggleFlagMode()
	{
		_flagMode = !_flagMode;
		StyleFlagButton();
		GD.Print($"[Mines] flag mode = {_flagMode}");
	}

	private void StyleFlagButton()
	{
		GameArt.StyleButton(_flagButton, _flagMode ? new Color("#ff8f6b") : new Color(1f, 1f, 1f, 0.12f),
			Colors.White, radius: 24, border: new Color(1f, 1f, 1f, 0.28f), fontSize: 32);
		_flagButton.Text = _flagMode ? "插旗模式 · 开" : "插旗模式 · 关";
		_hintLabel.Text = _flagMode
			? "点格子 = 插旗 · 长按也能插旗"
			: "点格子 = 挖开 · 长按格子 = 插旗";
	}

	private void Redraw() => _grid.QueueRedraw();

	// ===================== 输入 =====================

	/// <summary>
	/// 一次点击可能在引擎里被「模拟」成两种事件：项目开了 mouse→touch，而
	/// touch→mouse 又是引擎默认开的，于是点一下鼠标会同时收到一个真鼠标事件
	/// 和一个模拟触摸事件。两个都处理就会一下挖两格 / 旗子插了又取消。
	/// 引擎给这种模拟事件打的标记是 device = -1（DEVICE_ID_EMULATION），过滤掉即可。
	/// </summary>
	private static bool IsEmulated(InputEvent e) => e.Device == -1;

	/// <summary>
	/// 这里必须是 <c>_Input</c>，不能是 <c>_UnhandledInput</c>。
	///
	/// 棋盘画在 Node2D 上、不是 Control，事件只能靠代码自己接。而 <c>_UnhandledInput</c>
	/// 只在「没有任何 Control 认领这次点击」时才被调用 —— 本场景的根节点是个铺满全屏的
	/// Control（Control 默认 MouseFilter = Stop），它会替所有空白区域「认领」掉点击，
	/// 于是事件永远送不到处理函数，表现就是**点哪儿都没反应**。
	///
	/// 雷霆战机 / 羊了个羊 / 贴纸游戏用的都是 <c>_Input</c>，这里保持一致。
	/// 代价是点按钮时也会顺带进来一次：靠 <see cref="CellAt"/> 的范围判定 +
	/// <see cref="_phase"/> 状态拦掉，不会误挖到棋盘。
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventKey key when key.Pressed && !key.Echo &&
										 (key.Keycode == Key.Escape || key.Keycode == Key.F):
				// 手机上没有右键，键鼠党用 Esc / F 切插旗模式
				if (_phase == Phase.Play)
					ToggleFlagMode();
				GetViewport().SetInputAsHandled();
				break;

			case InputEventMouseMotion mm when !IsEmulated(mm):
				SetHover(mm.Position);
				if (_pressIndex >= 0 && mm.Position.DistanceTo(_pressPos) > DragCancelPx)
					CancelPress();
				break;

			case InputEventScreenDrag sd when !IsEmulated(sd):
				if (_pressIndex >= 0 && sd.Position.DistanceTo(_pressPos) > DragCancelPx)
					CancelPress();
				break;

			case InputEventMouseButton mb when !IsEmulated(mb) && mb.ButtonIndex == MouseButton.Left:
				if (mb.Pressed)
					BeginPress(mb.Position);
				else
					EndPress(mb.Position);
				break;

			case InputEventScreenTouch st when !IsEmulated(st):
				if (st.Pressed)
					BeginPress(st.Position);
				else
					EndPress(st.Position);
				break;
		}
	}

	private void BeginPress(Vector2 pos)
	{
		if (_phase != Phase.Play)
			return;
		int idx = CellAt(pos);
		if (idx < 0)
			return;
		_pressIndex = idx;
		_pressAt = _now;
		_pressPos = pos;
		_longFired = false;
		Redraw();
	}

	private void EndPress(Vector2 pos)
	{
		// 拖动过就被 CancelPress 清掉了，这里 _pressIndex < 0 直接返回
		if (_pressIndex >= 0 && !_longFired && CellAt(pos) == _pressIndex)
		{
			int idx = _pressIndex;
			CancelPress();
			PrimaryAction(idx);
			return;
		}
		CancelPress();
	}

	private void CancelPress()
	{
		if (_pressIndex >= 0)
		{
			_pressIndex = -1;
			Redraw();
		}
		_longFired = false;
	}

	/// <summary>按住不放 = 插旗。在计时器里判，不等抬手 —— 手指压着的时候旗子就该出来了。</summary>
	private void UpdateLongPress()
	{
		if (_pressIndex < 0 || _longFired)
			return;
		if (_now - _pressAt < LongPressSeconds)
			return;
		_longFired = true;
		ToggleMark(_pressIndex);
		_rings.Add(new Ring { Pos = CellCenter(_pressIndex), Tint = new Color("#ffd77a"), MaxR = _tile * 0.9f });
	}

	private void SetHover(Vector2 pos)
	{
		int idx = _phase == Phase.Play ? CellAt(pos) : -1;
		if (idx == _hoverIndex)
			return;
		_hoverIndex = idx;
		Redraw();
	}

	/// <summary>屏幕坐标 → 格子下标；不在棋盘上返回 -1。</summary>
	private int CellAt(Vector2 pos)
	{
		if (_cols <= 0)
			return -1;
		Vector2 local = pos - _grid.GlobalPosition;
		if (local.X < 0f || local.Y < 0f)
			return -1;
		int c = (int)(local.X / _tile);
		int r = (int)(local.Y / _tile);
		if (c >= _cols || r >= _rows)
			return -1;
		return r * _cols + c;
	}

	private Vector2 CellCenter(int idx)
		=> new((idx % _cols + 0.5f) * _tile, (idx / _cols + 0.5f) * _tile);

	/// <summary>把 idx 的 8 邻域填进 buf（不分配新表，buf 由调用方复用）。</summary>
	private void FillNeighbors(int idx, List<int> buf)
	{
		buf.Clear();
		int r = idx / _cols, c = idx % _cols;
		for (int dr = -1; dr <= 1; dr++)
		{
			int rr = r + dr;
			if (rr < 0 || rr >= _rows)
				continue;
			for (int dc = -1; dc <= 1; dc++)
			{
				if (dr == 0 && dc == 0)
					continue;
				int cc = c + dc;
				if (cc < 0 || cc >= _cols)
					continue;
				buf.Add(rr * _cols + cc);
			}
		}
	}

	// ===================== 棋盘绘制 =====================

	/// <summary>棋盘节点的全部内容。逻辑都在外面，它只负责画。</summary>
	private void DrawBoard(CanvasItem ci)
	{
		if (_cols <= 0)
		{
			// 还没选难度：棋盘位给一句提示，别让屏幕中间空着一大块
			if (_font != null)
				ci.DrawString(_font, new Vector2(0f, (BoardTop + BoardBottom) * 0.5f), "选个难度开始吧",
					HorizontalAlignment.Center, StageW, 40, new Color(1f, 1f, 1f, 0.40f));
			return;
		}

		float w = _tile * _cols, h = _tile * _rows;
		ci.DrawStyleBox(_sbPanel, new Rect2(-15f, -15f, w + 30f, h + 30f));

		for (int r = 0; r < _rows; r++)
		{
			for (int c = 0; c < _cols; c++)
			{
				int i = r * _cols + c;
				Cell cell = _cells[i];
				var rect = new Rect2(c * _tile + 1.5f, r * _tile + 1.5f, _tile - 3f, _tile - 3f);
				bool live = _phase == Phase.Play;
				bool pressed = live && i == _pressIndex && !_longFired;
				bool hover = live && i == _hoverIndex;

				if (cell.Open)
				{
					DrawOpenCell(ci, i, rect, cell);
					continue;
				}

				// 未挖开：底下是深色边、上面是浅色面，两层叠出「按一下就会下去」的凸起
				ci.DrawStyleBox(_sbEdge, rect);
				// 按下时面往里缩，看起来就凹进去了 —— 比换个颜色更直观
				float off = pressed ? 2.6f : 0f;
				var face = new Rect2(rect.Position + new Vector2(off, off),
					rect.Size - new Vector2(2.6f + off, 2.6f + off));
				ci.DrawStyleBox(hover && !pressed ? _sbFaceHover : _sbFace, face);

				switch (cell.Mark)
				{
					case Mark.Flag:
						DrawFlagOn(ci, rect, _phase == Phase.Lose && !cell.Mine);
						break;
					case Mark.Question:
						DrawText(ci, rect, "?", Colors.White, 0.60f);
						break;
				}
			}
		}

		// 挖开时的一下白闪
		foreach (var p in _pops)
		{
			float t = Mathf.Clamp(p.Age / PopLife, 0f, 1f);
			_sbPop.BgColor = new Color(1f, 1f, 1f, 0.30f * (1f - t));
			ci.DrawStyleBox(_sbPop, new Rect2(
				p.Index % _cols * _tile + 1.5f, p.Index / _cols * _tile + 1.5f, _tile - 3f, _tile - 3f));
		}

		// 爆炸 / 胜利光环
		foreach (var ring in _rings)
		{
			float t = Mathf.Clamp(ring.Age / RingLife, 0f, 1f);
			float rad = ring.MaxR * (0.25f + 0.75f * t);
			float alpha = 1f - t;
			ci.DrawCircle(ring.Pos, rad, new Color(ring.Tint.R, ring.Tint.G, ring.Tint.B, 0.20f * alpha));
			ci.DrawArc(ring.Pos, rad, 0f, Mathf.Tau, 30, new Color(1f, 1f, 1f, 0.85f * alpha), 4f, true);
		}
	}

	private void DrawOpenCell(CanvasItem ci, int idx, Rect2 rect, Cell cell)
	{
		if (cell.Mine)
		{
			bool boom = idx == _boomIndex;
			ci.DrawStyleBox(boom ? _sbBoom : _sbOpenMine, rect);
			GameArt.DrawMine(ci, rect.Size.X * 0.30f, rect.GetCenter());
			return;
		}

		ci.DrawStyleBox(_sbOpen, rect);
		if (cell.Adj > 0)
			DrawText(ci, rect, cell.Adj.ToString(), NumberColor(cell.Adj), 0.62f);
	}

	/// <summary>经典 1~8 配色，但整体提亮：这套颜色是给浅色背景配的，压在深蓝底上会发闷。</summary>
	private static Color NumberColor(int n) => n switch
	{
		1 => new Color("#8fd3ff"),
		2 => new Color("#79e08a"),
		3 => new Color("#ff8f9c"),
		4 => new Color("#c79bff"),
		5 => new Color("#ffb46b"),
		6 => new Color("#63e2d8"),
		7 => new Color("#ffffff"),
		_ => new Color("#aab6cf"),
	};

	private void DrawFlagOn(CanvasItem ci, Rect2 rect, bool wrong)
	{
		var center = rect.GetCenter();
		GameArt.DrawFlag(ci, rect.Size.X * 0.30f, center);
		if (!wrong)
			return;
		// 输的时候插错的旗子打个叉，一眼就能看出哪几面判断错了
		float r = rect.Size.X * 0.30f;
		var ink = new Color("#ff4d6d");
		ci.DrawLine(center + new Vector2(-r, -r), center + new Vector2(r, r), ink, 5f, true);
		ci.DrawLine(center + new Vector2(r, -r), center + new Vector2(-r, r), ink, 5f, true);
	}

	/// <summary>在格子里居中画一行字（垂直居中要自己按 ascent/descent 算基线）。</summary>
	private void DrawText(CanvasItem ci, Rect2 rect, string text, Color color, float scale)
	{
		if (_font == null)
			return;
		int size = Mathf.Max(10, (int)(rect.Size.X * scale));
		float ascent = _font.GetAscent(size);
		float descent = _font.GetDescent(size);
		float baseline = rect.GetCenter().Y + (ascent - descent) * 0.5f;
		ci.DrawString(_font, new Vector2(rect.Position.X, baseline), text,
			HorizontalAlignment.Center, rect.Size.X, size, color);
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

		GameArt.StyleButton(_homeButton, new Color("#8f7bff"), Colors.White, fontSize: 32);
		_homeButton.CustomMinimumSize = new Vector2(140, 80);
		_homeButton.Text = "返回";
		_homeButton.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);

		GameArt.StyleButton(_setupButton, new Color("#4fa8ff"), Colors.White, fontSize: 32);
		_setupButton.CustomMinimumSize = new Vector2(140, 80);
		_setupButton.Text = "换难度";
		_setupButton.Pressed += ShowSetup;

		GameArt.StyleButton(_restartButton, new Color("#ff8f6b"), Colors.White, fontSize: 32);
		_restartButton.CustomMinimumSize = new Vector2(140, 80);
		_restartButton.Text = "重开";
		_restartButton.Pressed += () =>
		{
			if (_cols > 0)
				StartGame(_levelIndex);
			else
				ShowSetup();
		};
	}

	private void BuildHud()
	{
		_hud = new HBoxContainer();
		_hud.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_hud.OffsetLeft = 24;
		_hud.OffsetTop = 108;
		_hud.OffsetRight = -24;
		_hud.OffsetBottom = 158;
		_hud.AddThemeConstantOverride("separation", 12);
		_hud.MouseFilter = MouseFilterEnum.Ignore;
		_ui.AddChild(_hud);

		_mineLabel = MakeHudLabel(HorizontalAlignment.Left, new Color("#ffd77a"));
		_timeLabel = MakeHudLabel(HorizontalAlignment.Center, Colors.White);
		_bestLabel = MakeHudLabel(HorizontalAlignment.Right, new Color(1, 1, 1, 0.62f));

		_levelLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
		GameArt.OutlineText(_levelLabel, Colors.White, 26, 6);
		_levelLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_levelLabel.GrowHorizontal = GrowDirection.Both;
		_levelLabel.OffsetTop = 160;
		_levelLabel.OffsetBottom = 204;
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
		GameArt.OutlineText(lb, color, 28, 6);
		_hud.AddChild(lb);
		return lb;
	}

	private void BuildFlagButton()
	{
		_flagButton = new Button();
		_flagButton.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		_flagButton.GrowHorizontal = GrowDirection.Both;
		_flagButton.GrowVertical = GrowDirection.Begin;
		_flagButton.OffsetLeft = 160;
		_flagButton.OffsetRight = -160;
		_flagButton.OffsetTop = -150;
		_flagButton.OffsetBottom = -66;
		_flagButton.Pressed += ToggleFlagMode;
		_ui.AddChild(_flagButton);

		_hintLabel = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		_hintLabel.AddThemeFontSizeOverride("font_size", 22);
		_hintLabel.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.5f));
		_hintLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		_hintLabel.GrowHorizontal = GrowDirection.Both;
		_hintLabel.GrowVertical = GrowDirection.Begin;
		_hintLabel.OffsetTop = -60;
		_hintLabel.OffsetBottom = -18;
		_ui.AddChild(_hintLabel);

		StyleFlagButton();
	}

	/// <summary>
	/// 刷新 HUD。这个方法每帧都会被调到，所以只在**数值变化**时才重建字符串：
	/// 每帧无条件拼 3 个字符串，等于每秒白扔 180 个临时对象。
	/// </summary>
	private void UpdateHud()
	{
		int secs = (int)_elapsed;
		if (secs != _hudSecs)
		{
			_hudSecs = secs;
			_timeLabel.Text = $"用时 {FormatTime(secs)}";
		}

		int left = _mines - _flags;
		if (left != _hudMines)
		{
			_hudMines = left;
			_mineLabel.Text = $"剩余 {left} 雷";
		}

		int best = _best[_levelIndex];
		if (best != _hudBest)
		{
			_hudBest = best;
			_bestLabel.Text = best > 0 ? $"最好 {FormatTime(best)}" : "最好 —";
		}

		if (_cols > 0)
		{
			var def = Levels[_levelIndex];
			_levelLabel.Text = $"{def.Name} · {_rows} × {_cols} · {_mines} 雷";
		}
	}

	private static string FormatTime(int secs) => $"{secs / 60:00}:{secs % 60:00}";

	/// <summary>
	/// 难度面板。三档难度做成一大条一大条的按钮（不是小 chip）：
	/// 这是进游戏前的第一个决定，得让人一眼看清「多大盘、多少雷、纪录多少」。
	/// </summary>
	private void BuildSetup()
	{
		_setup = new Control { MouseFilter = MouseFilterEnum.Ignore };
		_setup.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_setup.Visible = false;
		_ui.AddChild(_setup);

		var dim = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.66f),
			MouseFilter = MouseFilterEnum.Stop, // 挡住底下的棋盘，选难度时不能继续动手
		};
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_setup.AddChild(dim);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(0.08f, 0.10f, 0.19f, 0.98f), 34, new Color(1f, 1f, 1f, 0.20f));
		box.ContentMarginLeft = box.ContentMarginRight = 36;
		box.ContentMarginTop = box.ContentMarginBottom = 32;
		panel.AddThemeStyleboxOverride("panel", box);
		dim.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 16);
		panel.AddChild(col);

		var title = new Label { Text = "扫 雷", HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(title, Colors.White, 60, 8);
		col.AddChild(title);

		var sub = new Label
		{
			Text = "挖开所有空格 · 别踩到炸弹",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		sub.AddThemeFontSizeOverride("font_size", 23);
		sub.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.66f));
		col.AddChild(sub);

		for (int i = 0; i < Levels.Length; i++)
		{
			int idx = i;
			var b = new Button { CustomMinimumSize = new Vector2(560, 112) };
			b.Text = ""; // 内容全部由子节点排，按钮自己不画文字
			GameArt.StyleButton(b, new Color(1f, 1f, 1f, 0.09f), Colors.White, radius: 24,
				border: new Color(1f, 1f, 1f, 0.26f));
			b.AddThemeStyleboxOverride("hover", GameArt.MakeBox(new Color(1f, 1f, 1f, 0.18f), 24, new Color(1f, 1f, 1f, 0.5f)));
			b.AddThemeStyleboxOverride("pressed", GameArt.MakeBox(new Color(1f, 1f, 1f, 0.06f), 24, new Color(1f, 1f, 1f, 0.4f)));
			b.AddThemeStyleboxOverride("focus", GameArt.MakeBox(new Color(0f, 0f, 0f, 0f), 24));
			b.Pressed += () =>
			{
				GD.Print($"[Mines] difficulty -> {Levels[idx].Name}");
				StartGame(idx);
			};
			col.AddChild(b);
			_levelButtons[i] = b;

			// 按钮里的子节点必须 MouseFilter = Ignore，否则它们会把点击吃掉，按钮收不到 pressed
			var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
			row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			row.OffsetLeft = 28;
			row.OffsetRight = -28;
			row.OffsetTop = 12;
			row.OffsetBottom = -12;
			row.AddThemeConstantOverride("separation", 16);
			b.AddChild(row);

			var cellCol = new VBoxContainer
			{
				MouseFilter = MouseFilterEnum.Ignore,
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
			};
			cellCol.AddThemeConstantOverride("separation", 2);
			row.AddChild(cellCol);

			var name = new Label
			{
				Text = Levels[i].Name,
				MouseFilter = MouseFilterEnum.Ignore,
			};
			GameArt.OutlineText(name, Levels[i].Tint, 40, 6);
			cellCol.AddChild(name);

			var info = new Label { MouseFilter = MouseFilterEnum.Ignore };
			info.AddThemeFontSizeOverride("font_size", 22);
			info.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.72f));
			cellCol.AddChild(info);
			_levelSubs[i] = info;

			var best = new Label
			{
				MouseFilter = MouseFilterEnum.Ignore,
				VerticalAlignment = VerticalAlignment.Center,
			};
			best.AddThemeFontSizeOverride("font_size", 26);
			best.AddThemeColorOverride("font_color", new Color("#ffd77a"));
			row.AddChild(best);
			_levelBests[i] = best;
		}

		_resumeButton = new Button { Text = "继续这一局", CustomMinimumSize = new Vector2(560, 88) };
		GameArt.StyleButton(_resumeButton, new Color("#7ee787"), new Color("#123018"), fontSize: 32);
		_resumeButton.Pressed += ResumeGame;
		col.AddChild(_resumeButton);

		var quit = new Button { Text = "返回首页", CustomMinimumSize = new Vector2(560, 88) };
		GameArt.StyleButton(quit, new Color("#8f7bff"), Colors.White, fontSize: 32);
		quit.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);
		col.AddChild(quit);
	}

	private void ShowSetup()
	{
		// 只有在「一局正在进行」时才提供回到棋盘的入口：
		// 已经赢了/输了的那盘盘面已经揭开，继续下去没有意义
		_resumable = _phase == Phase.Play && _cols > 0;
		_phase = Phase.Setup;
		CancelPress();
		_result.Visible = false;
		RefreshSetup();
		_setup.Visible = true;
		UpdateHud();
		Redraw();
	}

	private void ResumeGame()
	{
		if (!_resumable)
			return;
		_setup.Visible = false;
		_phase = Phase.Play;
		Redraw();
	}

	private void RefreshSetup()
	{
		for (int i = 0; i < Levels.Length; i++)
		{
			var def = Levels[i];
			_levelSubs[i].Text = $"{def.Rows} × {def.Cols} · {def.Mines} 颗炸弹";
			_levelBests[i].Text = _best[i] > 0 ? $"最好\n{FormatTime(_best[i])}" : "最好\n—";
		}
		_resumeButton.Visible = _resumable;
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
		var box = GameArt.MakeBox(new Color(0.08f, 0.10f, 0.19f, 0.98f), 32, new Color(1f, 1f, 1f, 0.20f));
		box.ContentMarginLeft = box.ContentMarginRight = 40;
		box.ContentMarginTop = box.ContentMarginBottom = 32;
		panel.AddThemeStyleboxOverride("panel", box);
		_result.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 16);
		panel.AddChild(col);

		_resultTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(_resultTitle, Colors.White, 54, 8);
		col.AddChild(_resultTitle);

		_resultInfo = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_resultInfo.AddThemeFontSizeOverride("font_size", 30);
		_resultInfo.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.86f));
		col.AddChild(_resultInfo);

		_resultNew = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_resultNew.AddThemeFontSizeOverride("font_size", 30);
		_resultNew.AddThemeColorOverride("font_color", new Color("#ffd77a"));
		col.AddChild(_resultNew);

		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		row.AddThemeConstantOverride("separation", 16);
		col.AddChild(row);

		_againButton = MakeOverlayButton("再来一局", new Color("#ff8f6b"));
		_againButton.Pressed += () => StartGame(_levelIndex);
		row.AddChild(_againButton);

		var change = MakeOverlayButton("换难度", new Color("#4fa8ff"));
		change.Pressed += ShowSetup;
		row.AddChild(change);

		var quit = MakeOverlayButton("回首页", new Color("#8f7bff"));
		quit.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);
		row.AddChild(quit);
	}

	private Button MakeOverlayButton(string text, Color bg)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(200, 96) };
		GameArt.StyleButton(b, bg, Colors.White, fontSize: 30);
		return b;
	}

	// ===================== 最高纪录存档 =====================

	private void LoadBest()
	{
		var cfg = new ConfigFile();
		if (cfg.Load(BestPath) != Error.Ok)
			return;
		for (int i = 0; i < Levels.Length; i++)
			_best[i] = cfg.GetValue("best", Levels[i].Tag, 0).AsInt32();
	}

	private void SaveBest()
	{
		var cfg = new ConfigFile();
		for (int i = 0; i < Levels.Length; i++)
			cfg.SetValue("best", Levels[i].Tag, _best[i]);
		var err = cfg.Save(BestPath);
		if (err == Error.Ok)
			GD.Print($"[Mines] best saved: {DescribeBest()}");
		else
			GD.PushError($"[Mines] save best failed: {err}");
	}

	private string DescribeBest()
	{
		var parts = new List<string>();
		for (int i = 0; i < Levels.Length; i++)
			parts.Add($"{Levels[i].Tag}={(_best[i] > 0 ? FormatTime(_best[i]) : "—")}");
		return string.Join(" ", parts);
	}

	// ===================== 音效 =====================

	private void BuildSfxPool()
	{
		// 一个 AudioStreamPlayer 连着响会互相打断（贴纸游戏踩过这个坑），
		// 这里用 4 个轮流用，音色靠 PitchScale 变。
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
		if (_sfxPool.Count == 0)
			return;
		_sfxIndex = (_sfxIndex + 1) % _sfxPool.Count;
		var p = _sfxPool[_sfxIndex];
		p.PitchScale = pitch;
		p.VolumeDb = volumeDb;
		p.Play();
	}

	// ===================== 内嵌节点 =====================

	/// <summary>
	/// 棋盘的画布。逻辑全在外面的 <see cref="MinesweeperGame"/> 里，它只把 _Draw 转回去 ——
	/// 这样「数据 + 规则」在一个文件里是一条线，不用在两边来回跳。
	/// 注意：C# 里凡是继承 Godot 节点类型的类都必须写 partial，否则报 GD0001。
	/// </summary>
	private sealed partial class GridView : Node2D
	{
		private readonly MinesweeperGame _game;

		public GridView(MinesweeperGame game) => _game = game;

		public override void _Draw() => _game.DrawBoard(this);
	}

	// ===================== 自测 =====================
	//
	// 触发方式：项目根目录放 selftest.flag，内容写 mines，然后启动游戏
	// （首页会立刻切到本场景，本场景看到内容是自己就跑这一套）。

	private async Task RunSelfTestAsync()
	{
		GD.Print("[SELFTEST] begin (mines)");
		await Wait(0.4);

		int fails = 0;
		_seed = 20260923; // 固定雷区，跑出来的盘面每次一样，出问题好复现

		// ① 三档难度的行列和雷数：难度越高必须越大越多。
		//    注意雷是**第一次点击时才落的**（见 PlaceMines），所以要先挖一格再数才有意义。
		for (int i = 0; i < Levels.Length; i++)
		{
			StartGame(i);
			bool cfgOk = _rows == Levels[i].Rows && _cols == Levels[i].Cols && _mines == Levels[i].Mines;
			Reveal(_total / 2);
			int actual = CountMines();
			bool ok = cfgOk && actual == Levels[i].Mines;
			GD.Print($"[SELFTEST] level \"{Levels[i].Name}\" {_rows}×{_cols} · 配置 {_mines} 雷 · 实际 {actual} 雷 -> {ok}");
			if (!ok) fails++;
		}
		bool ramp = Levels[0].Rows < Levels[1].Rows && Levels[1].Rows < Levels[2].Rows &&
					Levels[0].Cols < Levels[1].Cols && Levels[1].Cols < Levels[2].Cols &&
					Levels[0].Mines < Levels[1].Mines && Levels[1].Mines < Levels[2].Mines;
		GD.Print($"[SELFTEST] 难度阶梯 行列+炸弹 都递增 -> {ramp}");
		if (!ramp) fails++;

		// ② 背景真的铺满了吗（露出 0.3 灰的清屏色就说明背景没覆盖整屏）
		var shot = GetViewport().GetTexture().GetImage();
		var px = new Vector2I((int)(shot.GetWidth() * 0.01f), (int)(shot.GetHeight() * 0.5f));
		var col = shot.GetPixel(px.X, px.Y);
		bool bgOk = !GameArt.IsClearColor(col);
		GD.Print($"[SELFTEST] background covers screen: pixel{px}={col} -> {bgOk}");
		if (!bgOk) fails++;

		// ③ 格子边长算得对：棋盘要装得进可用区，且不小于下限
		bool sizeOk = _tile >= MinTile && _tile * _cols <= StageW - PadX * 2f + 0.5f &&
					  _origin.Y >= BoardTop - 0.5f && _origin.Y + _tile * _rows <= BoardBottom + 0.5f;
		GD.Print($"[SELFTEST] grid fits: tile={_tile} origin={_origin} -> {sizeOk}");
		if (!sizeOk) fails++;

		// ④ 第一次点击必安全：首点所在的 3×3 里一颗雷都没有，而且会连片展开
		for (int i = 0; i < Levels.Length; i++)
		{
			StartGame(i);
			int center = (_rows / 2) * _cols + _cols / 2;
			Reveal(center);
			bool safe = !_cells[center].Mine;
			FillNeighbors(center, _nbBuf);
			foreach (int n in _nbBuf)
				if (_cells[n].Mine)
					safe = false;
			bool spread = _opened > 1; // 3×3 全空的盘面，首点一定带出一片
			GD.Print($"[SELFTEST] first click safe \"{Levels[i].Name}\": opened={_opened} -> safe={safe} spread={spread}");
			if (!safe) fails++;
			if (!spread) fails++;
		}

		// ⑤ 数字格只开它自己（不像 0 那样往外扩散）
		StartGame(0);
		Reveal(_total / 2);
		int numberIdx = FindOpenedWithAdj();
		if (numberIdx >= 0)
		{
			// 把它关回去再单独挖一次：只应该 +1 格
			_cells[numberIdx].Open = false;
			_opened--;
			int before = _opened;
			Reveal(numberIdx);
			bool single = _opened == before + 1;
			GD.Print($"[SELFTEST] number cell opens itself only: {before} -> {_opened} -> {single}");
			if (!single) fails++;
		}
		else
		{
			GD.Print("[SELFTEST] number cell opens itself only: 找不到数字格，跳过");
		}

		// ⑥ 插旗 / 问号三态循环 + 剩余雷数
		StartGame(0);
		int probe = _total - 1;
		ToggleMark(probe);
		bool f1 = _cells[probe].Mark == Mark.Flag && _flags == 1;
		ToggleMark(probe);
		bool f2 = _cells[probe].Mark == Mark.Question && _flags == 0;
		ToggleMark(probe);
		bool f3 = _cells[probe].Mark == Mark.None && _flags == 0;
		GD.Print($"[SELFTEST] mark cycles 无→旗→问号→无: {f1} {f2} {f3}");
		if (!f1 || !f2 || !f3) fails++;

		// ⑦ 插了旗的格子挖不动（旗子是玩家的判断，不能被一次误触掀掉）
		ToggleMark(probe);
		PrimaryAction(probe);
		bool protectedOk = !_cells[probe].Open && _cells[probe].Mark == Mark.Flag;
		GD.Print($"[SELFTEST] flagged cell protected: open={_cells[probe].Open} -> {protectedOk}");
		if (!protectedOk) fails++;

		// ⑧ 插旗模式下，点一下 = 插旗（不挖开）
		_flagMode = true;
		PrimaryAction(probe);
		bool modeOk = _cells[probe].Mark == Mark.Question && !_cells[probe].Open;
		_flagMode = false;
		GD.Print($"[SELFTEST] flag mode: mark={_cells[probe].Mark} open={_cells[probe].Open} -> {modeOk}");
		if (!modeOk) fails++;

		// ⑨ 数字格「连开」：周围旗子插对了，点数字会把剩下的邻居一起翻开
		StartGame(1);
		Reveal(_total / 2);
		int chordIdx = FindChordCandidate(out List<int> mineNeighbors, out List<int> safeNeighbors);
		if (chordIdx >= 0)
		{
			foreach (int m in mineNeighbors)
				ToggleMark(m);
			int openedBefore = _opened;
			int safeBefore = 0;
			foreach (int s in safeNeighbors)
				if (_cells[s].Open)
					safeBefore++;
			PrimaryAction(chordIdx);
			int safeOpened = 0;
			foreach (int s in safeNeighbors)
				if (_cells[s].Open)
					safeOpened++;
			bool chordOk = _phase == Phase.Win || (safeOpened > safeBefore && _phase == Phase.Play);
			GD.Print($"[SELFTEST] chord: opened {openedBefore}->{_opened} safe {safeBefore}->{safeOpened} phase={_phase} -> {chordOk}");
			if (!chordOk) fails++;
		}
		else
		{
			GD.Print("[SELFTEST] chord: 找不到合适点位，跳过");
		}

		// ⑩ 挖开所有非雷格子 = 通关：自动插旗 + 结算面板 + 纪录落盘
		//     先备份玩家原有的纪录：自测是「秒开全盘」，写进去的就是 1 秒，
		//     真去玩的人再也刷不掉这条假纪录，所以验完必须写回去。
		int[] keepBest = (int[])_best.Clone();
		StartGame(0);
		int lastSafe = -1;
		for (int i = 0; i < _total; i++)
		{
			if (_cells[i].Mine)
				continue;
			if (lastSafe < 0)
			{
				lastSafe = i;
				continue;
			}
			Reveal(i);
		}
		GD.Print($"[SELFTEST] before last: opened={_opened}/{_total - _mines} phase={_phase}");
		Reveal(lastSafe);
		await Wait(0.15);
		bool winOk = _phase == Phase.Win && _result.Visible && _againButton.Visible &&
					 _flags == _mines && _opened == _total - _mines;
		GD.Print($"[SELFTEST] win: phase={_phase} opened={_opened} flags={_flags}/{_mines} result={_result.Visible} -> {winOk}");
		if (!winOk) fails++;

		var cfg = new ConfigFile();
		int savedBest = cfg.Load(BestPath) == Error.Ok
			? cfg.GetValue("best", Levels[0].Tag, -1).AsInt32()
			: -1;
		bool persisted = savedBest == _best[0] && savedBest > 0;
		GD.Print($"[SELFTEST] best persisted: mem={_best[0]} file={savedBest} -> {persisted}");
		if (!persisted) fails++;

		SaveShot("mines_win"); // 先截图（这张要能看到刚拿到的纪录），再把它还原

		// 把纪录还原（含文件）：自测是「秒开全盘」，写进去的假成绩真去玩的人再也刷不掉
		System.Array.Copy(keepBest, _best, _best.Length);
		SaveBest();
		_hudBest = int.MinValue;
		UpdateHud();

		// ⑪ 踩雷：整盘雷翻开、插错的旗子被标出来、进结算
		StartGame(2);
		Reveal(_total / 2);
		// 故意在一个**还没挖开的非雷格**上插旗，输的时候它应该被判为「插错了」
		int wrongFlagAt = FindClosedSafeCell();
		if (wrongFlagAt >= 0)
			ToggleMark(wrongFlagAt);
		int boomAt = FindMine();
		Reveal(boomAt);
		await Wait(0.15);
		bool minesShown = true;
		for (int i = 0; i < _total; i++)
			if (_cells[i].Mine && !_cells[i].Open)
				minesShown = false;
		bool loseOk = _phase == Phase.Lose && _result.Visible && minesShown && _boomIndex == boomAt;
		GD.Print($"[SELFTEST] lose: phase={_phase} boom={_boomIndex}/{boomAt} allMinesShown={minesShown} " +
				 $"wrongFlags={_wrongFlags} -> {loseOk}");
		if (!loseOk) fails++;
		if (wrongFlagAt >= 0)
		{
			bool wrongOk = _wrongFlags == 1;
			GD.Print($"[SELFTEST] wrong flag counted: {_wrongFlags} -> {wrongOk}");
			if (!wrongOk) fails++;
		}

		SaveShot("mines_lose");

		// ⑫ 重开：状态必须干净（进度、用时、插旗都归零，雷区回到「还没落雷」的状态）
		StartGame(1);
		await Wait(0.1);
		bool restartOk = _phase == Phase.Play && _opened == 0 && _flags == 0 &&
						 (int)_elapsed == 0 && !_setup.Visible && !_result.Visible &&
						 _mines == Levels[1].Mines && CountMines() == 0;
		GD.Print($"[SELFTEST] restart: phase={_phase} opened={_opened} flags={_flags} " +
				 $"配置雷={_mines} 已布雷={CountMines()} -> {restartOk}");
		if (!restartOk) fails++;

		// ⑬ 选难度面板 + 「继续这一局」
		ShowSetup();
		await Wait(0.1);
		int level0Before = _levelIndex;
		bool setupOk = _setup.Visible && _phase == Phase.Setup && _resumeButton.Visible &&
					   _levelSubs[0].Text.Contains("9 × 9") && _levelBests[0].Text.Contains("最好");
		GD.Print($"[SELFTEST] setup panel: visible={_setup.Visible} resume={_resumeButton.Visible} " +
				 $"sub0=\"{_levelSubs[0].Text}\" best0=\"{_levelBests[0].Text}\" -> {setupOk}");
		if (!setupOk) fails++;
		SaveShot("mines_setup");

		ResumeGame();
		await Wait(0.1);
		bool resumeOk = !_setup.Visible && _phase == Phase.Play && _levelIndex == level0Before;
		GD.Print($"[SELFTEST] resume: setupVisible={_setup.Visible} phase={_phase} -> {resumeOk}");
		if (!resumeOk) fails++;

		// ⑭ 换难度真的换盘：点面板上的「困难」，尺寸和雷数都跟着变
		ShowSetup();
		_levelButtons[2].EmitSignal(BaseButton.SignalName.Pressed);
		await Wait(0.15);
		bool changeOk = _levelIndex == 2 && _rows == Levels[2].Rows && _cols == Levels[2].Cols &&
						_mines == Levels[2].Mines && CountMines() == 0 && !_setup.Visible;
		GD.Print($"[SELFTEST] change level -> \"{Levels[_levelIndex].Name}\" {_rows}×{_cols} " +
				 $"· {_mines} 雷 -> {changeOk}");
		if (!changeOk) fails++;

		// ⑮ 用真实输入事件走一遍：按下/抬手要能挖开格子
		StartGame(0);
		int tapIdx = _total - 1 - _cols; // 右下角往上一点，肯定在棋盘里
		int tapRow = tapIdx / _cols, tapCol = tapIdx % _cols;
		var tapPos = _origin + new Vector2((tapCol + 0.5f) * _tile, (tapRow + 0.5f) * _tile);
		int openedBeforeTap = _opened;
		PressAt(tapPos);
		await Wait(0.05);
		ReleaseAt(tapPos);
		await Wait(0.1);
		bool tapOk = _opened > openedBeforeTap;
		GD.Print($"[SELFTEST] real tap: {openedBeforeTap} -> {_opened} -> {tapOk}");
		if (!tapOk) fails++;

		// ⑯ 长按 = 插旗。点位必须挑一个**还没挖开**的格子，
		//    否则 ToggleMark 会因为「已挖开」直接返回，测出来的假阴性。
		int longIdx = FindClosedCell(avoidIdx: tapIdx);
		bool longOk = false;
		if (longIdx >= 0)
		{
			var longPos = _origin + new Vector2((longIdx % _cols + 0.5f) * _tile, (longIdx / _cols + 0.5f) * _tile);
			PressAt(longPos);
			await Wait(LongPressSeconds + 0.15); // 压着不动，超过长按阈值
			bool fired = _cells[longIdx].Mark == Mark.Flag;
			ReleaseAt(longPos);
			await Wait(0.1);
			longOk = fired && _cells[longIdx].Mark == Mark.Flag; // 抬手不应该再把这个旗子取消掉
			GD.Print($"[SELFTEST] long press -> flag: fired={fired} mark={_cells[longIdx].Mark} -> {longOk}");
		}
		else
		{
			GD.Print("[SELFTEST] long press -> flag: 找不到未挖开的格子，跳过");
		}
		if (!longOk) fails++;

		// ⑰ 模拟事件必须被过滤掉（不过滤的话一次点击会挖两格 / 旗子插了又取消）
		var emu = new InputEventScreenTouch { Position = tapPos, Pressed = true, Device = -1 };
		bool emuIgnored = IsEmulated(emu);
		GD.Print($"[SELFTEST] emulated event filtered: {emuIgnored}");
		if (!emuIgnored) fails++;

		// ⑱ 真实 GUI 链路：事件必须一路走到棋盘。
		//    ⑮ 是直接调用处理函数，永远测不出「事件半路被吃掉」这类问题 ——
		//    而实际玩的时候，点击先要过一遍引擎的 Control 命中测试，没人认领才会落到
		//    我们的处理函数上。所以这里把事件推给 Viewport，走完整条链。
		StartGame(0);
		await Wait(0.1);
		int guiIdx = _total / 2;
		var guiPos = _origin + new Vector2((guiIdx % _cols + 0.5f) * _tile, (guiIdx / _cols + 0.5f) * _tile);
		int openedBeforeGui = _opened;
		PushGuiClick(guiPos);
		await Wait(0.15);
		bool guiOk = _opened > openedBeforeGui;
		GD.Print($"[SELFTEST] gui pipeline opens cell: {openedBeforeGui} -> {_opened} -> {guiOk}");
		if (!guiOk) fails++;

		// ⑲ 把玩家真正会走的那条路整条跑一遍：难度面板上真实点一下「普通」开局，
		//    再真实点一下格子。这条断言覆盖的是「进游戏 → 选难度 → 点格子」的完整链路 ——
		//    之前这条链路上每一步都断着：事件到不了棋盘。
		ShowSetup();
		await Wait(0.12);
		PushGuiClick(_levelButtons[1].GetGlobalRect().GetCenter());
		await Wait(0.2);
		bool startByClickOk = !_setup.Visible && _phase == Phase.Play && _levelIndex == 1 &&
							  _cols == Levels[1].Cols && _rows == Levels[1].Rows && _opened == 0;
		GD.Print($"[SELFTEST] gui pipeline start game by clicking difficulty: " +
				 $"setupVisible={_setup.Visible} phase={_phase} level={Levels[_levelIndex].Name} " +
				 $"{_rows}×{_cols} -> {startByClickOk}");
		if (!startByClickOk) fails++;

		int guiIdx2 = _total / 2;
		var guiPos2 = _origin + new Vector2((guiIdx2 % _cols + 0.5f) * _tile, (guiIdx2 / _cols + 0.5f) * _tile);
		int openedBeforeGui2 = _opened;
		PushGuiClick(guiPos2);
		await Wait(0.15);
		bool playByClickOk = _opened > openedBeforeGui2;
		GD.Print($"[SELFTEST] gui pipeline play after picking difficulty: " +
				 $"{openedBeforeGui2} -> {_opened} -> {playByClickOk}");
		if (!playByClickOk) fails++;

		// ⑳ 再走一遍**操作系统的入口**：窗口像素坐标 + Input.parse_input_event。
		//    这条才是真玩家按鼠标时事件进引擎的路（也是唯一会触发引擎「模拟触摸」的路，
		//    正好顺带验证 device = -1 的过滤在真实链路里也生效）。
		int osIdx = FindClosedCell();
		if (osIdx >= 0)
		{
			var osPos = _origin + new Vector2((osIdx % _cols + 0.5f) * _tile, (osIdx / _cols + 0.5f) * _tile);
			var winPos = GetViewport().GetFinalTransform() * osPos;
			int openedBeforeOs = _opened;
			Input.ParseInputEvent(new InputEventMouseMotion { Position = winPos, GlobalPosition = winPos });
			Input.ParseInputEvent(new InputEventMouseButton
			{
				Position = winPos, GlobalPosition = winPos, ButtonIndex = MouseButton.Left, Pressed = true,
			});
			Input.ParseInputEvent(new InputEventMouseButton
			{
				Position = winPos, GlobalPosition = winPos, ButtonIndex = MouseButton.Left, Pressed = false,
			});
			await Wait(0.2);
			bool osOk = _opened > openedBeforeOs;
			GD.Print($"[SELFTEST] os pipeline opens cell: logical={osPos} window={winPos} " +
					 $"{openedBeforeOs} -> {_opened} -> {osOk}");
			if (!osOk) fails++;
		}
		else
		{
			GD.Print("[SELFTEST] os pipeline opens cell: 找不到未挖开的格子，跳过");
			fails++;
		}

		// ㉑ 同一条链路上，「插旗模式」按钮也得能按到（按钮收不到点击同样是「点了没反应」）
		bool flagModeBefore = _flagMode;
		int openedBeforeFlagButton = _opened;
		PushGuiClick(_flagButton.GetGlobalRect().GetCenter());
		await Wait(0.15);
		bool flagButtonOk = _flagMode != flagModeBefore && _opened == openedBeforeFlagButton;
		GD.Print($"[SELFTEST] gui pipeline flag button: mode {flagModeBefore} -> {_flagMode} " +
				 $"opened={_opened} -> {flagButtonOk}");
		if (!flagButtonOk) fails++;


		// ㉒ 开着插旗模式，再从系统入口点一次没挖开的格子：**一次点击只能让旗子的
		//    三态循环走一格**（无 → 旗）。引擎开着 mouse→touch，同一个动作会送进来
		//    两个事件，如果 device = -1 的过滤失效，这里就会推进成「无 → 旗 → 问号」。
		//    这是「模拟事件过滤」在真实链路上的端到端验证 —— 前面那条只测了判据本身。
		int osFlagIdx = FindClosedCell();
		if (osFlagIdx >= 0)
		{
			var osFlagPos = _origin + new Vector2((osFlagIdx % _cols + 0.5f) * _tile,
												  (osFlagIdx / _cols + 0.5f) * _tile);
			var winPos2 = GetViewport().GetFinalTransform() * osFlagPos;
			Input.ParseInputEvent(new InputEventMouseMotion { Position = winPos2, GlobalPosition = winPos2 });
			Input.ParseInputEvent(new InputEventMouseButton
			{
				Position = winPos2, GlobalPosition = winPos2, ButtonIndex = MouseButton.Left, Pressed = true,
			});
			Input.ParseInputEvent(new InputEventMouseButton
			{
				Position = winPos2, GlobalPosition = winPos2, ButtonIndex = MouseButton.Left, Pressed = false,
			});
			await Wait(0.2);
			bool singleClickOk = _cells[osFlagIdx].Mark == Mark.Flag;
			GD.Print($"[SELFTEST] os pipeline one click = one mark step: cell={osFlagIdx} " +
					 $"mark={_cells[osFlagIdx].Mark} (期望 Flag，若变成 Question 就是事件被处理了两遍) -> {singleClickOk}");
			if (!singleClickOk) fails++;
		}
		else
		{
			GD.Print("[SELFTEST] os pipeline one click = one mark step: 找不到未挖开的格子，跳过");
			fails++;
		}

		// ㉓ 正常玩一小会儿，截一张中局盘面。
		//    插旗模式在 ㉑ 里已经用真实点击打开了，这一张正好是「挖开的格子 + 插着的旗 +
		//    开态的按钮」同时可见的盘面。
		StartGame(1);
		for (int i = 0; i < _total; i++)
		{
			if (i % 3 == 0 && !_cells[i].Mine && _phase == Phase.Play)
				Reveal(i);
		}
		int shotFlag = FindClosedCell();
		if (shotFlag >= 0)
			ToggleMark(shotFlag);
		await Wait(0.35);
		SaveShot("mines_board");

		GD.Print(fails == 0 ? "[SELFTEST] PASSED" : $"[SELFTEST] FAILED ({fails} 项)");
		await Wait(0.4);
		GetTree().Quit();
	}

	// ---------- 自测用的小工具 ----------

	private int CountMines()
	{
		int n = 0;
		for (int i = 0; i < _total; i++)
			if (_cells[i].Mine)
				n++;
		return n;
	}

	private int FindMine()
	{
		for (int i = 0; i < _total; i++)
			if (_cells[i].Mine)
				return i;
		return -1;
	}

	private int FindClosedSafeCell()
	{
		for (int i = 0; i < _total; i++)
			if (!_cells[i].Open && !_cells[i].Mine && _cells[i].Mark == Mark.None)
				return i;
		return -1;
	}

	private int FindOpenedWithAdj()
	{
		for (int i = 0; i < _total; i++)
			if (_cells[i].Open && _cells[i].Adj > 0)
				return i;
		return -1;
	}

	/// <summary>找一个还没挖开的格子（绕开 avoidIdx，免得两次测试互相干扰）。</summary>
	private int FindClosedCell(int avoidIdx = -1)
	{
		for (int i = 0; i < _total; i++)
			if (i != avoidIdx && !_cells[i].Open && _cells[i].Mark == Mark.None)
				return i;
		return -1;
	}

	/// <summary>找一个「已挖开、且周围还有没翻开的非雷格」的数字格，用来测连开。</summary>
	private int FindChordCandidate(out List<int> mineNeighbors, out List<int> safeNeighbors)
	{
		mineNeighbors = new List<int>();
		safeNeighbors = new List<int>();
		for (int i = 0; i < _total; i++)
		{
			if (!_cells[i].Open || _cells[i].Adj <= 0)
				continue;
			FillNeighbors(i, _nbBuf);
			var around = new List<int>(_nbBuf);
			var mines = new List<int>();
			var safe = new List<int>();
			foreach (int n in around)
			{
				if (_cells[n].Mark == Mark.Flag)
					continue; // 已经插了旗的不算
				if (_cells[n].Mine)
					mines.Add(n);
				else if (!_cells[n].Open)
					safe.Add(n);
			}
			if (mines.Count != _cells[i].Adj || safe.Count == 0)
				continue; // 得是「旗子还没插全」或者「周围没得开」的点位
			mineNeighbors = mines;
			safeNeighbors = safe;
			return i;
		}
		return -1;
	}

	/// <summary>
	/// 自测里模拟输入：按下 / 抬手各推一个触摸事件。
	/// 注意这是**直接调处理函数**，绕过了 GUI 命中测试 —— 只能验证逻辑，
	/// 验证不了「事件能不能送到」。后者交给 <see cref="PushGuiClick"/>。
	/// </summary>
	private void PressAt(Vector2 pos)
		=> _Input(new InputEventScreenTouch { Position = pos, Pressed = true });

	private void ReleaseAt(Vector2 pos)
		=> _Input(new InputEventScreenTouch { Position = pos, Pressed = false });

	/// <summary>
	/// 推一次「真·点击」给视口：按下 + 抬手，并且连引擎的模拟触摸一起推，
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
		GD.Print($"[Mines] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}
}
