#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 雷霆战机 —— 竖版飞行射击（首页里的「飞行射击」）。
///
/// 和贴纸游戏最大的不同：这是一个**逐帧跑的游戏循环**。
/// 贴纸游戏全项目没有 _Process，纯粹是「点击 → 改状态 → 重画」；
/// 这里每帧都要推进子弹、敌机、道具、特效，所以 _Process 是主角（见 <see cref="UpdateLoop"/>）。
///
/// 结构上刻意保持「一个脚本 + 一个瘦场景」：
/// 场景文件里只有 Stage / Background / Entities / UI / TopBar 这几个骨架，
/// 战机、敌机、子弹、道具、特效、HUD、结算面板全部由代码创建。
///
/// 所有图形都是 _Draw 现画的（<see cref="GameArt"/>），不依赖任何素材文件。
/// </summary>
public partial class ThunderGame : Control
{
	// ================= 可调参数 =================
	private const float KeySpeed = 620f;         // 键盘移动速度（px/s）
	private const float PlayerRadius = 17f;      // 自机碰撞半径
	private const float FireInterval = 0.125f;   // 自动开火间隔（秒）
	private const float BulletSpeed = 1000f;     // 自机子弹速度
	private const float EnemyBulletSpeed = 330f; // 敌弹速度
	private const int MaxLives = 3;
	private const int MaxPower = 3;              // 火力等级：1 单发 / 2 双发 / 3 三发
	private const double InvulnSeconds = 1.7;    // 受击后的无敌时间
	private const double RampSeconds = 75.0;     // 难度爬升到顶所需时间
	private const float SpawnEasy = 1.15f;       // 开局出敌间隔
	private const float SpawnHard = 0.40f;       // 最难时出敌间隔
	private const float HudHeight = 108f;        // 顶栏区域高度（落在这里的按下不算操作自机）
	private const float PlayTop = 124f;          // 自机活动区上边界
	private const float PlayBottomPad = 56f;     // 自机活动区下边距
	private const float GunnerHoldY = 250f;      // 炮台机下降到这个高度就悬停
	private const float ShakeDecay = 34f;        // 震动衰减速度

	private const string SfxPath = "res://sfx/ding.wav";
	private const string BestScorePath = "user://thunder_best.cfg";

	// 分层（本场景里的 z_index 都是绝对层号，和贴纸游戏一样的写法）
	private const int ZStars = -10, ZEnemy = 10, ZPickup = 15, ZPlayer = 20, ZBullet = 30, ZFx = 40;

	// ================= 场景节点 =================
	private Control _stage = null!;
	private TextureRect _background = null!;
	private Node2D _entities = null!;
	private Control _ui = null!;
	private HBoxContainer _topBar = null!;
	private Button _homeButton = null!;
	private Button _pauseButton = null!;
	private Ship _player = null!;
	private StarField _stars = null!;
	private VBoxContainer _hud = null!;
	private Label _scoreLabel = null!;
	private Label _bestLabel = null!;
	private Label _livesLabel = null!;
	private Label _toast = null!;
	private ColorRect _dim = null!;
	private Label _overTitle = null!;
	private Label _overInfo = null!;
	private Label _overNew = null!;
	private Button _resumeButton = null!;
	private Button _retryButton = null!;
	private Button _returnButton = null!;

	// ================= 运行时状态 =================
	private readonly List<Bullet> _bullets = new();
	private readonly List<Enemy> _enemies = new();
	private readonly List<Pickup> _pickups = new();
	private readonly List<Fx> _fx = new();
	private readonly List<AudioStreamPlayer> _sfxPool = new();

	private readonly System.Random _rng = new();

	private Vector2 _playerPos;
	private Vector2 _lastPointer;
	private bool _pointerHeld;

	private bool _paused;
	private bool _gameOver;
	private int _lives = MaxLives;
	private int _score;
	private int _best;
	private int _power = 1;
	private double _invuln;
	private double _fireTimer;
	private double _spawnTimer;
	private double _elapsed;
	private float _shake;
	private int _sfxIndex;
	private int _shotCount;   // 只是用来给自测看「到底开过火没有」
	private int _hudScore = -1, _hudBest = -1, _hudLives = -1; // HUD 缓存，避免每帧拼字符串
	private Tween? _toastTween;

	/// <summary>画布宽度/高度。第一帧布局还没跑完时尺寸可能是 0，给个兜底值免得算出 NaN。</summary>
	private float StageW => Size.X > 0f ? Size.X : 720f;
	private float StageH => Size.Y > 0f ? Size.Y : 1280f;

	private enum EnemyKind { Scout, Weaver, Gunner }

	private enum OverlayMode { Paused, GameOver }

	public override void _Ready()
	{
		_stage = GetNode<Control>("Stage");
		_background = GetNode<TextureRect>("Stage/Background");
		_entities = GetNode<Node2D>("Stage/Entities");
		_ui = GetNode<Control>("UI");
		_topBar = GetNode<HBoxContainer>("UI/TopBar");
		_homeButton = GetNode<Button>("UI/TopBar/HomeButton");
		_pauseButton = GetNode<Button>("UI/TopBar/PauseButton");

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		Theme = GameArt.MakeUiTheme();
		SetProcessInput(true);

		// 背景：开局就是深空渐变（GradientTexture2D，GPU 侧，不占内存）
		_background.Texture = GameArt.VerticalGradient(new Color("#05060f"), new Color("#16204a"));
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
		_background.MouseFilter = MouseFilterEnum.Ignore;

		// 滚动星空 + 自机
		_stars = new StarField { ZIndex = ZStars };
		_entities.AddChild(_stars);
		_player = new Ship { ZIndex = ZPlayer };
		_entities.AddChild(_player);

		BuildSfxPool();
		BuildTopBar();
		BuildHud();
		BuildToast();
		BuildOverlay();
		LoadBest();
		StartRun();

		GetViewport().SizeChanged += Layout;
		Layout();

		GD.Print($"[Thunder] ready. best={_best} selftest={SelftestFlag.Describe()}");

		// 自测开关由首页路由过来：内容正好是 "thunder" 才跑本游戏的自测
		if (SelftestFlag.Read() == SelftestFlag.TokenThunder)
			_ = RunSelfTestAsync();
		else
			_ = ShowIntroAsync();
	}

	private void Layout()
	{
		// Control 不会自动铺满父节点，尺寸为 0 的话里面的东西全看不见
		_stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_stage.MouseFilter = MouseFilterEnum.Ignore;
		_ui.MouseFilter = MouseFilterEnum.Ignore;

		// 背景必须显式铺满 + 关掉「最小尺寸 = 纹理尺寸」：
		// TextureRect 的 min size 默认跟着纹理走，而渐变纹理只有 8×256，
		// 不铺满就只在左上角画一小块，其余全是视口清屏色（0.3 灰）。
		_background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_background.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;

		ClampPlayer();
	}

	// ================= 主循环 =================

	/// <summary>
	/// 游戏循环。顺序很重要：先让自机/武器动，再让敌人和子弹动，最后统一判碰撞。
	/// 暂停时整帧跳过（星空也停），结算后只保留余波（敌人继续飞、特效继续播），不再生成新东西。
	/// </summary>
	public override void _Process(double delta)
	{
		if (_paused)
			return;

		_stars.Scroll((float)delta);

		if (!_gameOver)
		{
			_elapsed += delta;
			UpdatePlayer(delta);
			UpdateWeapons(delta);
			UpdateSpawner(delta);
		}

		UpdateEnemies(delta);
		UpdateBullets(delta);
		UpdatePickups(delta);
		UpdateFx(delta);
		CollectPickups();

		if (!_gameOver)
			ResolveCollisions();

		_player.QueueRedraw();
		UpdateHud();
		UpdateShake(delta);
	}

	private void UpdatePlayer(double delta)
	{
		float dt = (float)delta;
		if (_invuln > 0)
			_invuln -= delta;

		// 键盘（桌面）
		Vector2 dir = Vector2.Zero;
		if (Input.IsKeyPressed(Key.Left) || Input.IsKeyPressed(Key.A)) dir.X -= 1f;
		if (Input.IsKeyPressed(Key.Right) || Input.IsKeyPressed(Key.D)) dir.X += 1f;
		if (Input.IsKeyPressed(Key.Up) || Input.IsKeyPressed(Key.W)) dir.Y -= 1f;
		if (Input.IsKeyPressed(Key.Down) || Input.IsKeyPressed(Key.S)) dir.Y += 1f;
		if (dir != Vector2.Zero)
			_playerPos += dir.Normalized() * KeySpeed * dt;

		ClampPlayer();
		_player.Position = _playerPos;

		// 尾焰抖动 + 无敌期闪烁
		_player.Flame = 0.75f + 0.35f * Mathf.Sin((float)_elapsed * 26f);
		_player.Modulate = _invuln > 0 && Mathf.Sin((float)_invuln * 26f) > 0f
			? new Color(1f, 1f, 1f, 0.35f)
			: Colors.White;
	}

	private void ClampPlayer()
	{
		_playerPos = new Vector2(
			Mathf.Clamp(_playerPos.X, 40f, StageW - 40f),
			Mathf.Clamp(_playerPos.Y, PlayTop, StageH - PlayBottomPad));
	}

	private void UpdateWeapons(double delta)
	{
		_fireTimer -= delta;
		if (_fireTimer > 0)
			return;
		_fireTimer = FireInterval;

		Vector2 muzzle = _playerPos + new Vector2(0f, -34f);
		switch (_power)
		{
			case 1:
				MakePlayerBullet(muzzle);
				break;
			case 2:
				MakePlayerBullet(muzzle + new Vector2(-13f, 6f));
				MakePlayerBullet(muzzle + new Vector2(13f, 6f));
				break;
			default:
				MakePlayerBullet(muzzle);
				MakePlayerBullet(muzzle + new Vector2(-18f, 10f), new Vector2(-180f, -BulletSpeed));
				MakePlayerBullet(muzzle + new Vector2(18f, 10f), new Vector2(180f, -BulletSpeed));
				break;
		}
		_shotCount++;
		PlaySfx(1.9f, -26f);
	}

	private void UpdateSpawner(double delta)
	{
		_spawnTimer -= delta;
		if (_spawnTimer > 0)
			return;

		float diff = Difficulty;
		_spawnTimer = Mathf.Lerp(SpawnEasy, SpawnHard, diff) * (0.85f + (float)_rng.NextDouble() * 0.3f);
		SpawnEnemy(PickKind(diff));
	}

	/// <summary>难度系数 0~1，随时间线性爬升。</summary>
	private float Difficulty => Mathf.Min(1f, (float)(_elapsed / RampSeconds));

	/// <summary>加权随机挑一种敌机：小机一直有，蛇形机逐渐变多，炮台机要过一阵才登场。</summary>
	private EnemyKind PickKind(float diff)
	{
		float wScout = 1.0f;
		float wWeaver = 0.15f + diff * 1.0f;
		float wGunner = Mathf.Max(0f, diff - 0.25f) * 1.1f;
		float r = (float)_rng.NextDouble() * (wScout + wWeaver + wGunner);
		if (r < wScout) return EnemyKind.Scout;
		if (r < wScout + wWeaver) return EnemyKind.Weaver;
		return EnemyKind.Gunner;
	}

	private void UpdateEnemies(double delta)
	{
		float dt = (float)delta;
		for (int i = _enemies.Count - 1; i >= 0; i--)
		{
			var e = _enemies[i];
			if (!GodotObject.IsInstanceValid(e))
			{
				_enemies.RemoveAt(i);
				continue;
			}

			e.Age += dt;
			if (e.FlashTimer > 0)
			{
				e.FlashTimer -= delta;
				if (e.FlashTimer <= 0)
					e.Modulate = Colors.White;
			}

			switch (e.Kind)
			{
				case EnemyKind.Scout:
					e.Position += new Vector2(0f, e.Speed * dt);
					break;

				case EnemyKind.Weaver:
					// 蛇形：横向按正弦摆动，纵向匀速下压
					e.Position = new Vector2(
						e.BaseX + Mathf.Sin(e.Age * 2.3f) * 92f,
						e.Position.Y + e.Speed * dt);
					break;

				default:
					// 炮台机：先降到 GunnerHoldY，然后左右巡航一边走一边打
					if (e.Position.Y < GunnerHoldY)
					{
						e.Position = new Vector2(e.BaseX, e.Position.Y + e.Speed * dt);
					}
					else
					{
						e.Holding = true;
						float x = e.Position.X + e.StrafeDir * 70f * dt;
						if (x < 70f) { x = 70f; e.StrafeDir = 1f; }
						if (x > StageW - 70f) { x = StageW - 70f; e.StrafeDir = -1f; }
						e.Position = new Vector2(x, e.Position.Y);
					}
					break;
			}

			// 悬停后的炮台机朝自机当前位置开火（瞄准射击，所以必须一直动）
			if (e.Kind == EnemyKind.Gunner && e.Holding && !_gameOver && _player.Visible)
			{
				e.FireTimer -= delta;
				if (e.FireTimer <= 0)
				{
					e.FireTimer = Mathf.Lerp(1.7f, 1.0f, Difficulty);
					Vector2 dir = (_playerPos - e.Position).Normalized();
					MakeEnemyBullet(e.Position + dir * 26f, dir * EnemyBulletSpeed);
					PlaySfx(0.72f, -22f);
				}
			}

			if (e.Position.Y > StageH + 90f)
			{
				Despawn(e);
				_enemies.RemoveAt(i);
			}
		}
	}

	private void UpdateBullets(double delta)
	{
		float dt = (float)delta;
		for (int i = _bullets.Count - 1; i >= 0; i--)
		{
			var b = _bullets[i];
			if (!GodotObject.IsInstanceValid(b))
			{
				_bullets.RemoveAt(i);
				continue;
			}
			b.Position += b.Vel * dt;
			if (b.Position.Y < -70f || b.Position.Y > StageH + 70f ||
				b.Position.X < -70f || b.Position.X > StageW + 70f)
			{
				Despawn(b);
				_bullets.RemoveAt(i);
			}
		}
	}

	private void UpdatePickups(double delta)
	{
		float dt = (float)delta;
		for (int i = _pickups.Count - 1; i >= 0; i--)
		{
			var p = _pickups[i];
			if (!GodotObject.IsInstanceValid(p))
			{
				_pickups.RemoveAt(i);
				continue;
			}
			p.Age += dt;
			p.Position = new Vector2(
				p.BaseX + Mathf.Sin(p.Age * 3.2f) * 26f,
				p.Position.Y + p.Vel.Y * dt);
			if (p.Position.Y > StageH + 60f)
			{
				Despawn(p);
				_pickups.RemoveAt(i);
			}
		}
	}

	private void UpdateFx(double delta)
	{
		float dt = (float)delta;
		for (int i = _fx.Count - 1; i >= 0; i--)
		{
			var f = _fx[i];
			if (!GodotObject.IsInstanceValid(f))
			{
				_fx.RemoveAt(i);
				continue;
			}
			f.Age += dt;
			f.QueueRedraw(); // 爆炸是逐帧膨胀的，必须重画
			if (f.Age >= f.Life)
			{
				Despawn(f);
				_fx.RemoveAt(i);
			}
		}
	}

	private void UpdateShake(double delta)
	{
		if (_shake <= 0f)
		{
			if (_entities.Position != Vector2.Zero)
				_entities.Position = Vector2.Zero;
			return;
		}
		_shake = Mathf.Max(0f, _shake - ShakeDecay * (float)delta);
		_entities.Position = new Vector2(
			(float)(_rng.NextDouble() * 2.0 - 1.0) * _shake,
			(float)(_rng.NextDouble() * 2.0 - 1.0) * _shake);
	}

	// ================= 碰撞 =================

	private static bool Hits(Vector2 a, float ra, Vector2 b, float rb)
	{
		float r = ra + rb;
		return a.DistanceSquaredTo(b) <= r * r;
	}

	private void ResolveCollisions()
	{
		// ① 自机子弹 × 敌机
		for (int i = _bullets.Count - 1; i >= 0; i--)
		{
			var b = _bullets[i];
			if (b.FromEnemy || !GodotObject.IsInstanceValid(b))
				continue;
			for (int j = _enemies.Count - 1; j >= 0; j--)
			{
				var e = _enemies[j];
				if (!GodotObject.IsInstanceValid(e))
					continue;
				if (!Hits(b.Position, 13f, e.Position, e.Radius))
					continue;
				Despawn(b);
				_bullets.RemoveAt(i);
				DamageEnemy(e, 1);
				break;
			}
		}

		bool vulnerable = _player.Visible && _invuln <= 0;
		if (!vulnerable)
			return;

		// ② 敌弹 × 自机
		for (int i = _bullets.Count - 1; i >= 0; i--)
		{
			var b = _bullets[i];
			if (!b.FromEnemy || !GodotObject.IsInstanceValid(b))
				continue;
			if (!Hits(b.Position, b.Radius, _playerPos, PlayerRadius))
				continue;
			Despawn(b);
			_bullets.RemoveAt(i);
			HitPlayer();
			return;
		}

		// ③ 敌机 × 自机（撞机：两边一起炸）
		for (int j = _enemies.Count - 1; j >= 0; j--)
		{
			var e = _enemies[j];
			if (!GodotObject.IsInstanceValid(e))
				continue;
			if (!Hits(_playerPos, PlayerRadius, e.Position, e.Radius))
				continue;
			DamageEnemy(e, 99);
			HitPlayer();
			return;
		}
	}

	/// <summary>道具收集。注意它和「自机受伤」无关，无敌期间也照样能捡。</summary>
	private void CollectPickups()
	{
		for (int i = _pickups.Count - 1; i >= 0; i--)
		{
			var p = _pickups[i];
			if (!GodotObject.IsInstanceValid(p))
			{
				_pickups.RemoveAt(i);
				continue;
			}
			if (!Hits(p.Position, 22f, _playerPos, PlayerRadius + 16f))
				continue;
			Despawn(p);
			_pickups.RemoveAt(i);
			TakePickup();
		}
	}

	private void DamageEnemy(Enemy e, int dmg)
	{
		e.Hp -= dmg;
		if (e.Hp > 0)
		{
			// 受击闪白：改 Modulate 就行，不用重画（_Draw 出来的图形也会跟着变亮）
			e.Modulate = new Color(1.9f, 1.9f, 1.9f);
			e.FlashTimer = 0.07;
			return;
		}

		_score += e.Points;
		SpawnFx(e.Position, e.Radius * 2.6f, new Color("#ffcf8a"));
		PlaySfx(0.55f, -14f);
		if (e.Kind == EnemyKind.Gunner && _rng.NextDouble() < 0.55)
			SpawnPickup(e.Position);
		Despawn(e);
		_enemies.Remove(e);
	}

	private void HitPlayer()
	{
		if (_invuln > 0 || _gameOver)
			return;

		_lives--;
		_power = Mathf.Max(1, _power - 1); // 经典设定：受击掉一级火力
		SpawnFx(_playerPos, 104f, new Color("#ff9c6e"));
		_shake = 18f;
		PlaySfx(0.42f, -8f);

		// 受击清屏：把场上的敌弹抹掉，否则刚复活就会被第二颗接着打死
		for (int i = _bullets.Count - 1; i >= 0; i--)
		{
			if (!_bullets[i].FromEnemy)
				continue;
			Despawn(_bullets[i]);
			_bullets.RemoveAt(i);
		}

		_invuln = InvulnSeconds;
		UpdateHud();
		if (_lives <= 0)
			GameOver();
	}

	private void TakePickup()
	{
		if (_power < MaxPower)
		{
			_power++;
			ShowToast($"火力升级 ×{_power}");
		}
		else
		{
			_score += 500;
			ShowToast("火力已满 +500");
		}
		PlaySfx(1.35f, -12f);
		UpdateHud();
	}

	// ================= 生成事物 =================

	private Bullet MakePlayerBullet(Vector2 pos, Vector2? vel = null)
	{
		var b = new Bullet
		{
			FromEnemy = false,
			Vel = vel ?? new Vector2(0f, -BulletSpeed),
			Radius = 7f,
			Position = pos,
			ZIndex = ZBullet,
		};
		_entities.AddChild(b);
		_bullets.Add(b);
		return b;
	}

	private Bullet MakeEnemyBullet(Vector2 pos, Vector2 vel)
	{
		var b = new Bullet
		{
			FromEnemy = true,
			Vel = vel,
			Radius = 9f,
			Position = pos,
			ZIndex = ZBullet,
		};
		_entities.AddChild(b);
		_bullets.Add(b);
		return b;
	}

	/// <summary>生成一架敌机。<paramref name="at"/> 为 null 时随机横坐标从屏幕上方进入。</summary>
	private Enemy SpawnEnemy(EnemyKind kind, Vector2? at = null)
	{
		float speedUp = 1f + Difficulty * 0.55f;
		float x = at?.X ?? (90f + (float)_rng.NextDouble() * (StageW - 180f));
		var e = new Enemy { Kind = kind, ZIndex = ZEnemy };
		switch (kind)
		{
			case EnemyKind.Scout:
				e.Hp = 1; e.Radius = 19f; e.Points = 100; e.Speed = 250f * speedUp;
				e.Position = at ?? new Vector2(x, -40f);
				break;
			case EnemyKind.Weaver:
				e.Hp = 2; e.Radius = 21f; e.Points = 200; e.Speed = 175f * speedUp;
				e.BaseX = x;
				e.Position = at ?? new Vector2(x, -50f);
				break;
			default:
				e.Hp = 4; e.Radius = 26f; e.Points = 300; e.Speed = 130f * speedUp;
				e.BaseX = x;
				e.Position = at ?? new Vector2(x, -60f);
				break;
		}
		e.FireTimer = 0.9;
		_entities.AddChild(e);
		_enemies.Add(e);
		return e;
	}

	private void SpawnPickup(Vector2 pos)
	{
		var p = new Pickup
		{
			Position = pos,
			BaseX = pos.X,
			Vel = new Vector2(0f, 105f),
			ZIndex = ZPickup,
		};
		_entities.AddChild(p);
		_pickups.Add(p);
	}

	private void SpawnFx(Vector2 pos, float radius, Color tint)
	{
		var f = new Fx { Position = pos, MaxRadius = radius, Tint = tint, ZIndex = ZFx };
		_entities.AddChild(f);
		_fx.Add(f);
	}

	private static void Despawn(Node n)
	{
		if (!GodotObject.IsInstanceValid(n))
			return;
		// 先 RemoveChild 再 QueueFree：只 QueueFree 的话要到帧末才真的删掉，
		// 这一帧它还挂在容器里（布局会多算一份）。
		n.GetParent()?.RemoveChild(n);
		n.QueueFree();
	}

	private void ClearEntities()
	{
		foreach (var b in _bullets) Despawn(b);
		foreach (var e in _enemies) Despawn(e);
		foreach (var p in _pickups) Despawn(p);
		foreach (var f in _fx) Despawn(f);
		_bullets.Clear();
		_enemies.Clear();
		_pickups.Clear();
		_fx.Clear();
		_entities.Position = Vector2.Zero;
	}

	// ================= 一局的开始与结束 =================

	private void StartRun()
	{
		ClearEntities();
		_lives = MaxLives;
		_score = 0;
		_power = 1;
		_elapsed = 0;
		_invuln = 1.0; // 开局给一秒无敌，免得一出来就被贴着打
		_fireTimer = 0;
		_spawnTimer = 1.3;
		_shake = 0f;
		_gameOver = false;
		_paused = false;
		_playerPos = new Vector2(StageW * 0.5f, StageH - 180f);
		_player.Position = _playerPos;
		_player.Visible = true;
		_player.Modulate = Colors.White;
		_dim.Visible = false;
		_pauseButton.Text = "暂停";
		UpdateHud();
	}

	private void GameOver()
	{
		_gameOver = true;
		_player.Visible = false;
		bool newRecord = _score > _best;
		if (newRecord)
		{
			_best = _score;
			SaveBest();
		}
		ShowOverlay("游戏结束", $"本局得分 {_score}　　最高分 {_best}",
			newRecord ? "★ 新纪录！" : "", OverlayMode.GameOver);
		PlaySfx(0.35f, -6f);
	}

	private void TogglePause()
	{
		if (_gameOver)
			return;
		_paused = !_paused;
		_pauseButton.Text = _paused ? "继续" : "暂停";
		if (_paused)
			ShowOverlay("已暂停", "按 Esc 或点「继续」回到游戏", "", OverlayMode.Paused);
		else
			_dim.Visible = false;
	}

	private void ShowOverlay(string title, string info, string extra, OverlayMode mode)
	{
		// 结算/暂停面板要独占视线：先把可能还在飘的提示条收掉。
		// 提示条的 ZIndex=100，比遮罩高，不收掉就会明晃晃地压在「再来一局」按钮上
		// （吃过道具 1.45 秒内就挂掉，一定会撞上）。
		_toastTween?.Kill();
		_toast.Visible = false;

		_overTitle.Text = title;
		_overInfo.Text = info;
		_overNew.Text = extra;
		_overNew.Visible = extra.Length > 0;
		_resumeButton.Visible = mode == OverlayMode.Paused;
		_retryButton.Visible = mode == OverlayMode.GameOver;
		_dim.Visible = true;
	}

	// ================= 最高分存档 =================

	private void LoadBest()
	{
		var cfg = new ConfigFile();
		if (cfg.Load(BestScorePath) == Error.Ok)
			_best = cfg.GetValue("score", "best", 0).AsInt32();
	}

	private void SaveBest()
	{
		var cfg = new ConfigFile();
		cfg.SetValue("score", "best", _best);
		var err = cfg.Save(BestScorePath);
		if (err == Error.Ok)
			GD.Print($"[Thunder] best saved: {_best}");
		else
			GD.PushError($"[Thunder] save best failed: {err}");
	}

	// ================= 输入 =================

	/// <summary>
	/// 一套手势同时管鼠标和触摸：按下记坐标，移动时**按位移量**搬动自机。
	/// 用位移量而不是「瞬移到手指下」，是为了避免手指一点屏幕战机就跳过去。
	/// 落在顶栏区域的按下不接管，否则点「暂停」会把战机拽跑。
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventKey key when key.Pressed && !key.Echo && key.Keycode == Key.Escape:
				TogglePause();
				GetViewport().SetInputAsHandled();
				break;
			case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
				if (mb.Pressed) BeginPointer(mb.Position);
				else _pointerHeld = false;
				break;
			case InputEventMouseMotion mm:
				PointerMove(mm.Position);
				break;
			case InputEventScreenTouch st:
				if (st.Pressed) BeginPointer(st.Position);
				else _pointerHeld = false;
				break;
			case InputEventScreenDrag sd:
				PointerMove(sd.Position);
				break;
		}
	}

	private void BeginPointer(Vector2 pos)
	{
		if (_paused || _gameOver || !_player.Visible)
			return;
		if (pos.Y < HudHeight)
			return;
		_pointerHeld = true;
		_lastPointer = pos;
	}

	private void PointerMove(Vector2 pos)
	{
		if (!_pointerHeld || _paused || _gameOver)
			return;
		_playerPos += pos - _lastPointer;
		_lastPointer = pos;
		ClampPlayer();
	}

	// ================= 音效 =================

	private void BuildSfxPool()
	{
		// 贴纸游戏只有一个 AudioStreamPlayer，连着开枪会互相打断。
		// 这里做 4 个轮流用，同一瞬间可以有 4 个声音叠着响；音色靠 PitchScale 变。
		var stream = GD.Load<AudioStream>(SfxPath);
		for (int i = 0; i < 4; i++)
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

	// ================= HUD / 提示 / 结算面板 =================

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

		GameArt.StyleButton(_pauseButton, new Color("#4fa8ff"), Colors.White, fontSize: 32);
		_pauseButton.CustomMinimumSize = new Vector2(140, 80);
		_pauseButton.Text = "暂停";
		_pauseButton.Pressed += TogglePause;
	}

	private void BuildHud()
	{
		_hud = new VBoxContainer();
		_hud.SetAnchorsAndOffsetsPreset(LayoutPreset.TopRight);
		// 锚在右边时，grow 必须是 Begin（向左长）。默认的 End 会让内容从右边缘往右溢出、被裁掉。
		_hud.GrowHorizontal = GrowDirection.Begin;
		_hud.OffsetLeft = -330;
		_hud.OffsetRight = -20;
		_hud.OffsetTop = 22;
		_hud.OffsetBottom = 170;
		_hud.AddThemeConstantOverride("separation", 0);
		_hud.MouseFilter = MouseFilterEnum.Ignore;
		_ui.AddChild(_hud);

		_scoreLabel = MakeHudLabel(36, Colors.White);
		_bestLabel = MakeHudLabel(24, new Color(1, 1, 1, 0.66f));
		_livesLabel = MakeHudLabel(28, new Color("#ff9ec4"));
	}

	private Label MakeHudLabel(int fontSize, Color color)
	{
		var lb = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Right,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		GameArt.OutlineText(lb, color, fontSize, 6);
		_hud.AddChild(lb);
		return lb;
	}

	/// <summary>
	/// 刷新 HUD 文字。只在数值变化时才重建字符串 —— 这个方法每帧都会被调用，
	/// 每帧无条件拼 3 个字符串等于每秒白扔 180 个临时对象。
	/// </summary>
	private void UpdateHud()
	{
		if (_hudScore != _score)
		{
			_hudScore = _score;
			_scoreLabel.Text = $"得分 {_score}";
		}
		int shownBest = Mathf.Max(_best, _score);
		if (_hudBest != shownBest)
		{
			_hudBest = shownBest;
			_bestLabel.Text = $"最高 {shownBest}";
		}
		if (_hudLives != _lives)
		{
			_hudLives = _lives;
			_livesLabel.Text = $"生命 × {Mathf.Max(_lives, 0)}";
		}
	}

	private void BuildToast()
	{
		_toast = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
			Visible = false,
			ZIndex = 100,
		};
		GameArt.OutlineText(_toast, Colors.White, 36, 8);
		_toast.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
		_toast.GrowHorizontal = GrowDirection.Both; // 水平向两侧扩，否则会从中心往右溢出被裁
		_toast.GrowVertical = GrowDirection.End;    // 垂直向下扩，顶边钉死
		// 不钉在屏幕正中：正中是敌机和弹幕的主战场，提示条会正好挡住来袭的子弹。
		// 放到自机默认位置（y≈1100）的上方，读起来像「自机身上的反馈」。
		_toast.OffsetTop = 980f;
		_toast.OffsetBottom = 980f;
		_ui.AddChild(_toast);
	}

	private void ShowToast(string msg)
	{
		_toastTween?.Kill();
		_toast.Text = msg;
		_toast.Visible = true;
		_toast.Modulate = Colors.White;
		_toastTween = CreateTween();
		_toastTween.TweenInterval(1.1);
		_toastTween.TweenProperty(_toast, "modulate:a", 0f, 0.35);
		_toastTween.TweenCallback(Callable.From(() => _toast.Visible = false));
	}

	private void BuildOverlay()
	{
		_dim = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.62f),
			MouseFilter = MouseFilterEnum.Stop, // 挡住底下的点击，暂停时不能操作自机
			Visible = false,
		};
		_dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_ui.AddChild(_dim);

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		panel.GrowHorizontal = GrowDirection.Both;
		panel.GrowVertical = GrowDirection.Both;
		var box = GameArt.MakeBox(new Color(0.09f, 0.10f, 0.16f, 0.97f), 30);
		box.ContentMarginLeft = box.ContentMarginRight = 44;
		box.ContentMarginTop = box.ContentMarginBottom = 36;
		panel.AddThemeStyleboxOverride("panel", box);
		_dim.AddChild(panel);

		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 20);
		panel.AddChild(col);

		_overTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		GameArt.OutlineText(_overTitle, Colors.White, 60, 8);
		col.AddChild(_overTitle);

		_overInfo = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_overInfo.AddThemeFontSizeOverride("font_size", 34);
		_overInfo.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.88f));
		col.AddChild(_overInfo);

		_overNew = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		_overNew.AddThemeFontSizeOverride("font_size", 32);
		_overNew.AddThemeColorOverride("font_color", new Color("#ffd77a"));
		col.AddChild(_overNew);

		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		row.AddThemeConstantOverride("separation", 22);
		col.AddChild(row);

		_resumeButton = MakeOverlayButton("继续", new Color("#4fa8ff"));
		_resumeButton.Pressed += TogglePause;
		row.AddChild(_resumeButton);

		_retryButton = MakeOverlayButton("再来一局", new Color("#ff8f6b"));
		_retryButton.Pressed += StartRun;
		row.AddChild(_retryButton);

		_returnButton = MakeOverlayButton("返回首页", new Color("#8f7bff"));
		_returnButton.Pressed += () => GetTree().ChangeSceneToFile(ScenePaths.Home);
		row.AddChild(_returnButton);
	}

	private Button MakeOverlayButton(string text, Color bg)
	{
		var b = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(250, 100),
		};
		GameArt.StyleButton(b, bg, Colors.White, fontSize: 34);
		return b;
	}

	/// <summary>开局提示（自测模式下不弹）。</summary>
	private async Task ShowIntroAsync()
	{
		await Wait(0.5);
		ShowToast("拖动屏幕 / 方向键移动 · 自动开火");
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}

	// ================= 内嵌节点 =================
	//
	// 都是「纯数据 + 自己画自己」的哑节点：位置和状态由外面的游戏循环推进，
	// 它们只负责 _Draw。这样整个游戏逻辑只在一个文件里，读起来是一条线。
	// 注意每个继承 Godot 节点的类都必须 partial，否则报 GD0001。

	private sealed partial class Ship : Node2D
	{
		public float Flame = 1f;

		public override void _Draw() => GameArt.DrawShip(this, 1f, Flame);
	}

	private sealed partial class Bullet : Node2D
	{
		public bool FromEnemy;
		public Vector2 Vel;
		public float Radius = 7f;

		public override void _Draw()
		{
			if (FromEnemy)
			{
				// 敌弹：粉色实心球 + 亮边，在深色星空里很显眼
				DrawCircle(Vector2.Zero, Radius, new Color("#ff6ad5"));
				DrawArc(Vector2.Zero, Radius, 0, Mathf.Tau, 22, new Color("#ffe6f8"), 2.5f, true);
				DrawCircle(Vector2.Zero, Radius * 0.4f, Colors.White);
			}
			else
			{
				// 自机子弹：细长光条
				DrawRect(new Rect2(-Radius * 0.55f, -Radius * 2.2f, Radius * 1.1f, Radius * 4.4f),
					new Color("#9fe9ff"));
				DrawRect(new Rect2(-Radius * 0.22f, -Radius * 2.6f, Radius * 0.44f, Radius * 5.2f),
					Colors.White);
			}
		}
	}

	private sealed partial class Enemy : Node2D
	{
		public EnemyKind Kind;
		public float Hp;
		public float Radius;
		public int Points;
		public float Speed;
		public float Age;
		public float BaseX;
		public float StrafeDir = 1f;
		public bool Holding;          // 炮台机是否已降到位、进入悬停状态
		public double FireTimer;
		public double FlashTimer;

		public override void _Draw()
		{
			switch (Kind)
			{
				case EnemyKind.Scout: GameArt.DrawScout(this); break;
				case EnemyKind.Weaver: GameArt.DrawWeaver(this); break;
				default: GameArt.DrawGunner(this); break;
			}
		}
	}

	private sealed partial class Pickup : Node2D
	{
		public Vector2 Vel;
		public float BaseX;
		public float Age;

		public override void _Draw() => GameArt.DrawPickup(this);
	}

	private sealed partial class Fx : Node2D
	{
		public float Age;
		public float Life = 0.42f;
		public float MaxRadius = 50f;
		public Color Tint = new Color("#ffd27f");

		public override void _Draw()
		{
			float t = Mathf.Clamp(Age / Life, 0f, 1f);
			float r = MaxRadius * (0.28f + 0.72f * t);
			float a = 1f - t;
			// 这里的 alpha 是真的会混合的（_Draw 走 GPU 绘制），
			// 和贴纸游戏里 Image.FillRect 那种「直接写像素、不混合」完全不是一回事。
			DrawCircle(Vector2.Zero, r, new Color(Tint.R, Tint.G, Tint.B, 0.5f * a));
			DrawArc(Vector2.Zero, r * 1.06f, 0, Mathf.Tau, 28, new Color(1f, 1f, 1f, 0.85f * a), 3f, true);
			DrawCircle(Vector2.Zero, r * 0.42f, new Color(1f, 0.95f, 0.8f, 0.9f * a));
		}
	}

	/// <summary>滚动星空：3 层不同速度/大小/亮度，营造「在往前飞」的感觉。</summary>
	private sealed partial class StarField : Node2D
	{
		private struct Star
		{
			public float X;
			public float Y;
			public float Speed;
			public float Size;
			public Color C;
		}

		private Star[] _stars = System.Array.Empty<Star>();

		public override void _Ready()
		{
			var rng = new System.Random(7);
			var list = new List<Star>();
			for (int layer = 0; layer < 3; layer++)
			{
				int count = 40 - layer * 8;
				float speed = 42f + layer * 62f;
				float size = 1.6f + layer * 1.1f;
				float alpha = 0.35f + layer * 0.27f;
				for (int i = 0; i < count; i++)
				{
					list.Add(new Star
					{
						X = rng.Next(0, 720),
						Y = rng.Next(0, 1280),
						Speed = speed * (0.85f + rng.NextSingle() * 0.3f),
						Size = size,
						C = new Color(1f, 1f, 1f, alpha),
					});
				}
			}
			_stars = list.ToArray();
		}

		/// <summary>由外面的游戏循环驱动（不用自己的 _Process，暂停时才能真正停住）。</summary>
		public void Scroll(float dt)
		{
			for (int i = 0; i < _stars.Length; i++)
			{
				_stars[i].Y += _stars[i].Speed * dt;
				if (_stars[i].Y > 1300f)
					_stars[i].Y = -8f;
			}
			QueueRedraw();
		}

		public override void _Draw()
		{
			foreach (var s in _stars)
				DrawRect(new Rect2(s.X, s.Y, s.Size, s.Size * 2.2f), s.C);
		}
	}

	// ================= 自测模式 =================
	//
	// 触发方式：项目根目录放 selftest.flag，内容写 thunder，然后启动游戏
	// （首页会立刻切到本场景，本场景看到内容是自己就跑这一套）。

	private async Task RunSelfTestAsync()
	{
		GD.Print("[SELFTEST] begin (thunder)");
		await Wait(0.5);

		int fails = 0;

		// ① 自机就位
		bool playerOk = GodotObject.IsInstanceValid(_player) && _player.IsInsideTree() && _player.Visible;
		GD.Print($"[SELFTEST] player ready: {playerOk} pos={_playerPos}");
		if (!playerOk) fails++;

		// ①b 背景真的铺满了吗（露出 0.3 灰的清屏色就说明背景没覆盖整屏）
		var firstShot = GetViewport().GetTexture().GetImage();
		var probePx = new Vector2I((int)(firstShot.GetWidth() * 0.04f), (int)(firstShot.GetHeight() * 0.5f));
		var probeCol = firstShot.GetPixel(probePx.X, probePx.Y);
		bool bgOk = !GameArt.IsClearColor(probeCol);
		GD.Print($"[SELFTEST] background covers screen: pixel{probePx}={probeCol} -> {bgOk}");
		if (!bgOk) fails++;

		// ② 三种敌机都能生成
		ClearEntities();
		SpawnEnemy(EnemyKind.Scout);
		SpawnEnemy(EnemyKind.Weaver);
		SpawnEnemy(EnemyKind.Gunner);
		await Wait(0.2);
		bool spawnOk = _enemies.Count == 3 &&
					   _enemies[0].Kind == EnemyKind.Scout &&
					   _enemies[1].Kind == EnemyKind.Weaver &&
					   _enemies[2].Kind == EnemyKind.Gunner;
		GD.Print($"[SELFTEST] spawn 3 kinds: count={_enemies.Count} -> {spawnOk}");
		if (!spawnOk) fails++;

		// ③ 敌机会往下走（说明游戏循环在推进）
		float ey0 = _enemies[0].Position.Y;
		await Wait(0.25);
		bool enemyMoves = GodotObject.IsInstanceValid(_enemies[0]) && _enemies[0].Position.Y > ey0 + 20f;
		GD.Print($"[SELFTEST] scout descends: {ey0:0.#} -> {(_enemies.Count > 0 && GodotObject.IsInstanceValid(_enemies[0]) ? _enemies[0].Position.Y : -1f):0.#} -> {enemyMoves}");
		if (!enemyMoves) fails++;

		// ④ 自动开火：子弹会自己冒出来，而且往上飞
		ClearEntities();
		int shots0 = _shotCount;
		var probe = MakePlayerBullet(new Vector2(200f, 900f));
		float by0 = probe.Position.Y;
		await Wait(0.3);
		float by1 = GodotObject.IsInstanceValid(probe) ? probe.Position.Y : -1f;
		bool fireOk = _shotCount > shots0 && _bullets.Count > 0;
		bool bulletMoves = by0 - by1 > 150f;
		GD.Print($"[SELFTEST] auto fire: shots {shots0}->{_shotCount}, bullets={_bullets.Count} -> {fireOk}");
		GD.Print($"[SELFTEST] bullet flies up: {by0:0.#} -> {by1:0.#} -> {bulletMoves}");
		if (!fireOk) fails++;
		if (!bulletMoves) fails++;

		// ⑤ 打中敌机 → 敌机消失并加分
		//    先关掉出敌：不然后面几项断言里会混进「自然刷出来的敌机被打死」的分数
		ClearEntities();
		_spawnTimer = 9999.0;
		_score = 0;
		var target = SpawnEnemy(EnemyKind.Scout, new Vector2(300f, 300f));
		MakePlayerBullet(new Vector2(300f, 360f));
		await Wait(0.2);
		bool killOk = !GodotObject.IsInstanceValid(target) && _score == 100;
		GD.Print($"[SELFTEST] kill scout: alive={GodotObject.IsInstanceValid(target)} score={_score} -> {killOk}");
		if (!killOk) fails++;

		// ⑥ 撞机 → 掉一条命 + 进入无敌期
		ClearEntities();
		_invuln = 0;
		int lives0 = _lives;
		SpawnEnemy(EnemyKind.Scout, _playerPos + new Vector2(0f, -6f));
		await Wait(0.2);
		bool ramOk = _lives == lives0 - 1 && _invuln > 0;
		GD.Print($"[SELFTEST] rammed: lives {lives0} -> {_lives}, invuln={_invuln:0.##} -> {ramOk}");
		if (!ramOk) fails++;

		// ⑦ 无敌期内不会被连续打掉
		int lives1 = _lives;
		SpawnEnemy(EnemyKind.Scout, _playerPos + new Vector2(0f, -6f));
		await Wait(0.2);
		bool invulnOk = _lives == lives1;
		GD.Print($"[SELFTEST] invulnerable: lives {lives1} -> {_lives} -> {invulnOk}");
		if (!invulnOk) fails++;

		// ⑧ 吃道具 → 火力升级
		ClearEntities();
		_power = 1;
		SpawnPickup(_playerPos);
		await Wait(0.25);
		bool powerOk = _power == 2;
		GD.Print($"[SELFTEST] pickup -> power {_power} (期待 2) -> {powerOk}");
		if (!powerOk) fails++;

		// ⑨ 暂停 / 继续
		TogglePause();
		await Wait(0.15);
		bool pauseOk = _paused && _dim.Visible && _resumeButton.Visible;
		TogglePause();
		await Wait(0.15);
		bool resumeOk = !_paused && !_dim.Visible;
		GD.Print($"[SELFTEST] pause={pauseOk} resume={resumeOk}");
		if (!pauseOk || !resumeOk) fails++;

		// ⑩ 结算 + 最高分落盘（写完之后再从文件读回来核对，确认真存进去了）
		ClearEntities();
		_invuln = 0;
		_lives = 1;
		_score = 12345;
		ShowToast("火力升级 ×2"); // 故意让提示条还挂在屏幕上：吃过道具 1.45 秒内挂掉就会这样
		HitPlayer();
		await Wait(0.3);
		bool overOk = _gameOver && _dim.Visible && _retryButton.Visible && !_resumeButton.Visible;
		GD.Print($"[SELFTEST] game over: over={_gameOver} dim={_dim.Visible} retry={_retryButton.Visible} -> {overOk}");
		if (!overOk) fails++;

		// 提示条必须被收掉：它的 ZIndex=100 比遮罩高，留着就会把字糊在结算按钮上
		bool toastGone = !_toast.Visible;
		GD.Print($"[SELFTEST] toast hidden at game over: {toastGone}");
		if (!toastGone) fails++;

		SaveShot("thunder_over");

		var cfg = new ConfigFile();
		bool savedOk = cfg.Load(BestScorePath) == Error.Ok &&
					   cfg.GetValue("score", "best", -1).AsInt32() == _best && _best >= 12345;
		GD.Print($"[SELFTEST] best persisted: best={_best} reload={savedOk} -> {savedOk}");
		if (!savedOk) fails++;

		// ⑪ 再来一局要真的重开
		StartRun();
		await Wait(0.5);
		bool restartOk = !_gameOver && _lives == MaxLives && _score == 0 && _power == 1 && !_dim.Visible;
		GD.Print($"[SELFTEST] restart: lives={_lives} score={_score} power={_power} over={_gameOver} -> {restartOk}");
		if (!restartOk) fails++;

		// ⑫ 正常玩一会儿再截一张（这时候场上应该有敌机、子弹、星空）
		await Wait(2.0);
		SaveShot("thunder_play");

		GD.Print(fails == 0 ? "[SELFTEST] PASSED" : $"[SELFTEST] FAILED ({fails} 项)");
		await Wait(0.4);
		GetTree().Quit();
	}

	private void SaveShot(string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		string path = $"user://screenshots/{tag}_{stamp}.png";
		var err = img.SavePng(path);
		GD.Print($"[Thunder] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
	}
}
