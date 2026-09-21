#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 贴纸换装游戏主控。
/// 分层渲染（固定 ZIndex，防止穿模）：背景(-10) < 身体(0) < 下装(10) < 上衣(11) < 鞋子(12) < 配饰(20)。
/// 穿着类（上衣/下装/鞋子/背景）单选；配饰类多选开关，可自由布置场景。
/// </summary>
public partial class Main : Control
{
	// ---------- 分层 ZIndex 常量 ----------
	private const int ZBackground = -10;
	private const int ZDoll = 0;
	private const int ZBottom = 10;
	private const int ZTop = 11;
	private const int ZShoes = 12;
	private const int ZProps = 20;

	private const string AssetDir = "res://assset/bedtime-and-morning-paper-doll-kit/";
	private const string DingPath = "res://sfx/ding.wav";

	private const float DollScale = 0.8f;
	private const float PanelHeight = 372f;

	// ---------- 场景节点 ----------
	private TextureRect _background = null!;
	private Node2D _character = null!;
	private Sprite2D _doll = null!;
	private Sprite2D _bottom = null!;
	private Sprite2D _top = null!;
	private Sprite2D _slipperLeft = null!;
	private Sprite2D _slipperRight = null!;
	private Node2D _props = null!;
	private HBoxContainer _topBar = null!;
	private HBoxContainer _categoryTabs = null!;
	private ScrollContainer _itemScroll = null!;
	private HBoxContainer _itemStrip = null!;
	private Button _resetButton = null!;
	private Button _photoButton = null!;
	private AudioStreamPlayer _sfx = null!;
	private Label _toast = null!;

	// ---------- 部件数据模型 ----------
	private sealed class WearItem
	{
		public string Label = "";
		public string? Tex;       // null = 脱下
		public Vector2 Offset;   // 相对角色中心的偏移（已含缩放）
		public float Scale = 1f;
	}

	private sealed class PropItem
	{
		public string Label = "";
		public string Tex = "";
		public Vector2 StagePos; // 舞台绝对坐标
		public float Scale = 1f;
		public Sprite2D? Sprite;
		public bool On;
	}

	private sealed class BgItem
	{
		public string Label = "";
		public Color Top = Colors.White;
		public Color Bottom = Colors.White;
		public string Decor = "none";
	}

	private readonly List<WearItem> _tops = new()
	{
		new WearItem { Label = "脱掉", Tex = null },
		new WearItem { Label = "睡衣上衣", Tex = AssetDir + "purple_pajama_top.png", Offset = new Vector2(0, -31), Scale = DollScale },
		new WearItem { Label = "浴袍", Tex = AssetDir + "blue_bathrobe.png", Offset = new Vector2(0, 24), Scale = DollScale },
	};

	private readonly List<WearItem> _bottoms = new()
	{
		new WearItem { Label = "脱掉", Tex = null },
		new WearItem { Label = "睡裤", Tex = AssetDir + "purple_pajama_pants.png", Offset = new Vector2(0, 205), Scale = DollScale },
	};

	// 鞋子是左右一双，作为同分类的一个部件
	private readonly Vector2 _slipperLeftOffset = new(-55, 225);
	private readonly Vector2 _slipperRightOffset = new(55, 225);

	private readonly List<PropItem> _propItems = new()
	{
		new() { Label = "枕头", Tex = "pillow.png", StagePos = new Vector2(135, 750), Scale = 0.85f },
		new() { Label = "台灯", Tex = "bedside_lamp.png", StagePos = new Vector2(100, 470), Scale = 0.85f },
		new() { Label = "闹钟", Tex = "alarm_clock.png", StagePos = new Vector2(625, 195), Scale = 0.85f },
		new() { Label = "睡前故事", Tex = "bedtime_book.png", StagePos = new Vector2(150, 630), Scale = 0.85f },
		new() { Label = "早餐盘", Tex = "breakfast_tray.png", StagePos = new Vector2(565, 715), Scale = 0.85f },
		new() { Label = "水果碗", Tex = "fruit_bowl.png", StagePos = new Vector2(500, 640), Scale = 0.9f },
		new() { Label = "橙汁", Tex = "orange_juice.png", StagePos = new Vector2(655, 590), Scale = 0.9f },
		new() { Label = "吐司", Tex = "toast.png", StagePos = new Vector2(620, 545), Scale = 0.9f },
		new() { Label = "牙刷", Tex = "toothbrush.png", StagePos = new Vector2(85, 330), Scale = 0.9f },
		new() { Label = "牙膏", Tex = "toothpaste.png", StagePos = new Vector2(175, 310), Scale = 0.9f },
		new() { Label = "书包", Tex = "backpack.png", StagePos = new Vector2(610, 390), Scale = 0.8f },
	};

	private readonly List<BgItem> _bgs = new()
	{
		new BgItem { Label = "夜晚", Top = new Color("#0e1637"), Bottom = new Color("#4a3f7a"), Decor = "stars" },
		new BgItem { Label = "清晨", Top = new Color("#ff9d5c"), Bottom = new Color("#fff3dd"), Decor = "sun_low" },
		new BgItem { Label = "白天", Top = new Color("#4aa8e8"), Bottom = new Color("#e8f7ff"), Decor = "clouds" },
		new BgItem { Label = "晚霞", Top = new Color("#ff7e6e"), Bottom = new Color("#ffd9a8"), Decor = "sunset" },
	};

	// ---------- 运行时状态 ----------
	private readonly List<ImageTexture> _bgTextures = new();
	private readonly Dictionary<Button, int> _categoryIndex = new();
	private int _activeCategory; // 0上衣 1下装 2鞋子 3配饰 4背景
	private int _topIndex = 1;   // 初始：睡衣上衣
	private int _bottomIndex = 1; // 初始：睡裤
	private int _shoeIndex = 1;  // 初始：拖鞋
	private int _bgIndex = 0;    // 初始：夜晚
	private Vector2 _center;
	private Tween? _toastTween;
	private int _photoCounter;

	public override void _Ready()
	{
		// 获取场景节点
		_background = GetNode<TextureRect>("Stage/Background");
		_character = GetNode<Node2D>("Stage/Character");
		_doll = GetNode<Sprite2D>("Stage/Character/Doll");
		_bottom = GetNode<Sprite2D>("Stage/Character/Bottom");
		_top = GetNode<Sprite2D>("Stage/Character/Top");
		_slipperLeft = GetNode<Sprite2D>("Stage/Character/Shoes/SlipperLeft");
		_slipperRight = GetNode<Sprite2D>("Stage/Character/Shoes/SlipperRight");
		_props = GetNode<Node2D>("Stage/Character/Props");
		_topBar = GetNode<HBoxContainer>("UI/TopBar");
		_categoryTabs = GetNode<HBoxContainer>("UI/BottomPanel/VBox/CategoryTabs");
		_itemScroll = GetNode<ScrollContainer>("UI/BottomPanel/VBox/ItemScroll");
		_itemStrip = GetNode<HBoxContainer>("UI/BottomPanel/VBox/ItemScroll/ItemStrip");
		_resetButton = GetNode<Button>("UI/TopBar/ResetButton");
		_photoButton = GetNode<Button>("UI/TopBar/PhotoButton");
		_sfx = GetNode<AudioStreamPlayer>("SfxPlayer");
		_toast = GetNode<Label>("Toast");

		// 音效
		_sfx.Stream = GD.Load<AudioStream>(DingPath);

		// 背景纹理预生成
		for (int i = 0; i < _bgs.Count; i++)
			_bgTextures.Add(MakeBackgroundTexture(_bgs[i]));

		BuildTheme();
		BuildUi();
		EnsurePropSprites();

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		LayoutStage();
		ResetLook(silent: true);
		SelectCategory(0, silent: true);

		GetViewport().SizeChanged += LayoutStage;

		GD.Print($"[Main] ready. user:// = {ProjectSettings.GlobalizePath("user://")}");

		if (FileAccess.FileExists("res://selftest.flag"))
			_ = RunSelfTestAsync();
	}

	// ================= 布局 =================

	private void LayoutStage()
	{
		Vector2 size = Size.X > 0 && Size.Y > 0 ? Size : new Vector2(720, 1280);

		// 关键：让 Stage 与 UI 容器铺满全屏。
		// UI 容器尺寸为 0 时，其内 TopBar/BottomPanel 的宽度会塌缩为 0，导致顶部按钮与底部标签全部不可见。
		GetNode<Control>("Stage").SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		GetNode<Control>("UI").SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		float stageH = Mathf.Max(size.Y - PanelHeight, 400);
		_center = new Vector2(size.X * 0.5f, stageH * 0.54f);
		_character.Position = _center;
		_doll.Texture = GD.Load<Texture2D>(AssetDir + "boy_doll.png");
		_doll.Scale = Vector2.One * DollScale;

		// 刷新穿着部件与配饰位置
		ApplyTop(_topIndex, silent: true);
		ApplyBottom(_bottomIndex, silent: true);
		ApplyShoes(_shoeIndex, silent: true);
		foreach (var p in _propItems)
		{
			if (p.Sprite != null)
				p.Sprite.Position = p.StagePos - _center;
		}
	}

	// ================= 主题与 UI 构建 =================

	private void BuildTheme()
	{
		var theme = new Theme();
		var font = new SystemFont
		{
			FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC", "sans-serif" },
		};
		theme.DefaultFont = font;
		theme.DefaultFontSize = 32;
		var ui = GetNode<Control>("UI");
		ui.Theme = theme;
		ui.MouseFilter = MouseFilterEnum.Ignore;
		GetNode<Control>("Stage").MouseFilter = MouseFilterEnum.Ignore;
		_background.MouseFilter = MouseFilterEnum.Ignore;
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
	}

	private static StyleBoxFlat MakeBox(Color bg, float radius = 18, Color? border = null)
	{
		var sb = new StyleBoxFlat
		{
			BgColor = bg,
			CornerRadiusTopLeft = (int)radius,
			CornerRadiusTopRight = (int)radius,
			CornerRadiusBottomLeft = (int)radius,
			CornerRadiusBottomRight = (int)radius,
			ContentMarginLeft = 14,
			ContentMarginRight = 14,
			ContentMarginTop = 8,
			ContentMarginBottom = 8,
		};
		if (border != null)
		{
			sb.BorderColor = border.Value;
			sb.BorderWidthLeft = sb.BorderWidthRight = sb.BorderWidthTop = sb.BorderWidthBottom = 5;
		}
		return sb;
	}

	private void StyleButton(Button b, Color bg, Color font, float radius = 18, Color? border = null)
	{
		var sb = MakeBox(bg, radius, border);
		b.AddThemeStyleboxOverride("normal", sb);
		b.AddThemeStyleboxOverride("hover", MakeBox(bg.Lightened(0.12f), radius, border));
		b.AddThemeStyleboxOverride("pressed", MakeBox(bg.Darkened(0.18f), radius, border));
		b.AddThemeColorOverride("font_color", font);
		b.AddThemeColorOverride("font_hover_color", font);
		b.AddThemeColorOverride("font_pressed_color", font);
		b.AddThemeColorOverride("font_focus_color", font);
		b.AddThemeFontSizeOverride("font_size", 34);
		b.PivotOffset = new Vector2(0.5f, 0.5f); // 会随 size 更新，点击弹跳用
	}

	private void BuildUi()
	{
		// ---- 顶栏：重置（左） 拍照（右） ----
		_topBar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_topBar.OffsetLeft = 20;
		_topBar.OffsetTop = 20;
		_topBar.OffsetRight = -20;
		_topBar.OffsetBottom = 112;
		_topBar.AddThemeConstantOverride("separation", 16);

		StyleButton(_resetButton, new Color("#ff8f6b"), Colors.White);
		_resetButton.CustomMinimumSize = new Vector2(170, 92);
		_resetButton.Text = "重置";
		_resetButton.Pressed += OnResetPressed;

		var spacer = new Control();
		spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_topBar.AddChild(spacer);
		_topBar.MoveChild(spacer, 1);

		StyleButton(_photoButton, new Color("#4fa8ff"), Colors.White);
		_photoButton.CustomMinimumSize = new Vector2(170, 92);
		_photoButton.Text = "拍照";
		_photoButton.Pressed += OnPhotoPressed;

		// ---- 底部面板 ----
		var panel = GetNode<PanelContainer>("UI/BottomPanel");
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		panel.OffsetTop = -PanelHeight;
		panel.OffsetLeft = 0;
		panel.OffsetRight = 0;
		panel.OffsetBottom = 0;
		var panelBox = MakeBox(new Color(1, 1, 1, 0.96f), 26);
		panelBox.ContentMarginLeft = 16;
		panelBox.ContentMarginRight = 16;
		panelBox.ContentMarginTop = 10;
		panelBox.ContentMarginBottom = 18;
		panel.AddThemeStyleboxOverride("panel", panelBox);

		var vbox = GetNode<VBoxContainer>("UI/BottomPanel/VBox");
		vbox.AddThemeConstantOverride("separation", 12);

		// ---- 分类标签 ----
		_categoryTabs.AddThemeConstantOverride("separation", 12);
		string[] cats = { "上衣", "下装", "鞋子", "配饰", "背景" };
		for (int i = 0; i < cats.Length; i++)
		{
			var b = new Button
			{
				Text = cats[i],
				CustomMinimumSize = new Vector2(0, 84),
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			};
			b.Pressed += () =>
			{
				PlayPop(b);
				SelectCategory(_categoryIndex[b]);
			};
			_categoryTabs.AddChild(b);
			_categoryIndex[b] = i;
		}

		// ---- 物品栏（横向滚动，默认自动隐藏滚动条，支持触摸拖动） ----
		_itemScroll.CustomMinimumSize = new Vector2(0, 190);
		_itemStrip.AddThemeConstantOverride("separation", 16);

		// ---- Toast 提示 ----
		_toast.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		_toast.HorizontalAlignment = HorizontalAlignment.Center;
		_toast.MouseFilter = MouseFilterEnum.Ignore;
		_toast.AddThemeFontSizeOverride("font_size", 42);
		_toast.AddThemeColorOverride("font_color", Colors.White);
		_toast.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.65f));
		_toast.AddThemeConstantOverride("outline_size", 10);
		var toastBox = MakeBox(new Color(0.12f, 0.12f, 0.16f, 0.82f), 24);
		toastBox.ContentMarginLeft = toastBox.ContentMarginRight = 28;
		toastBox.ContentMarginTop = toastBox.ContentMarginBottom = 14;
		_toast.AddThemeStyleboxOverride("normal", toastBox);
		_toast.Visible = false;
	}

	private void RefreshCategoryTabs()
	{
		foreach (var kv in _categoryIndex)
		{
			bool active = kv.Value == _activeCategory;
			Color bg = active ? new Color("#ffb347") : new Color("#f0f0f5");
			Color font = active ? Colors.White : new Color("#44445a");
			StyleButton(kv.Key, bg, font, radius: 20, border: active ? new Color("#e08a00") : null);
		}
	}

	// ================= 物品栏 =================

	private void SelectCategory(int index, bool silent = false)
	{
		_activeCategory = index;
		RefreshCategoryTabs();
		RebuildItemStrip();
		if (!silent) PlayDing();
	}

	private void RebuildItemStrip()
	{
		foreach (var child in _itemStrip.GetChildren())
			child.QueueFree();

		switch (_activeCategory)
		{
			case 0: // 上衣
				BuildWearStrip(_tops, _topIndex, i => ApplyTop(i));
				break;
			case 1: // 下装
				BuildWearStrip(_bottoms, _bottomIndex, i => ApplyBottom(i));
				break;
			case 2: // 鞋子
				BuildWearStrip(new List<WearItem>
				{
					new() { Label = "脱掉", Tex = null },
					new() { Label = "拖鞋", Tex = AssetDir + "purple_slipper_left.png" },
				}, _shoeIndex, i => ApplyShoes(i));
				break;
			case 3: // 配饰（多选开关）
				BuildPropStrip();
				break;
			case 4: // 背景
				BuildBgStrip();
				break;
		}
	}

	private void BuildWearStrip(List<WearItem> items, int currentIndex, System.Action<int> apply)
	{
		for (int i = 0; i < items.Count; i++)
		{
			var item = items[i];
			if (item.Tex is null)
			{
				var b = new Button
				{
					Text = "脱掉",
					CustomMinimumSize = new Vector2(150, 150),
				};
				StyleButton(b, new Color("#f0f0f5"), new Color("#44445a"), radius: 22);
				b.AddThemeFontSizeOverride("font_size", 38);
				int idx = i;
				b.Pressed += () =>
				{
					PlayPop(b);
					apply(idx);
				};
				_itemStrip.AddChild(b);
			}
			else
			{
				var tb = MakeItemButton(item.Label, item.Tex);
				int idx = i;
				tb.Pressed += () =>
				{
					PlayPop(tb);
					apply(idx);
				};
				_itemStrip.AddChild(tb);
				tb.Modulate = idx == currentIndex ? new Color(1f, 0.92f, 0.45f) : Colors.White;
			}
		}
	}

	private void BuildPropStrip()
	{
		foreach (var p in _propItems)
		{
			var tb = MakeItemButton(p.Label, AssetDir + p.Tex);
			var item = p;
			tb.Pressed += () =>
			{
				PlayPop(tb);
				ToggleProp(item);
			};
			_itemStrip.AddChild(tb);
			tb.Modulate = item.On ? new Color(1f, 0.92f, 0.45f) : Colors.White;
		}
	}

	private void BuildBgStrip()
	{
		for (int i = 0; i < _bgs.Count; i++)
		{
			var bg = _bgs[i];
			var b = new Button
			{
				Text = bg.Label,
				CustomMinimumSize = new Vector2(150, 150),
				ExpandIcon = true,
				IconAlignment = HorizontalAlignment.Center,
				VerticalIconAlignment = VerticalAlignment.Top,
			};
			// 生成小缩略图作为图标
			var thumb = MakeBackgroundTexture(bg, 150, 96);
			b.Icon = thumb;
			b.AddThemeColorOverride("font_color", Colors.White);
			b.AddThemeColorOverride("font_hover_color", Colors.White);
			b.AddThemeColorOverride("font_pressed_color", Colors.White);
			b.AddThemeColorOverride("font_focus_color", Colors.White);
			b.AddThemeFontSizeOverride("font_size", 30);
			b.AddThemeStyleboxOverride("normal", MakeBox(new Color(1, 1, 1, 0.2f), 22));
			b.AddThemeStyleboxOverride("hover", MakeBox(new Color(1, 1, 1, 0.32f), 22));
			b.AddThemeStyleboxOverride("pressed", MakeBox(new Color(1, 1, 1, 0.1f), 22));
			int idx = i;
			b.Pressed += () =>
			{
				PlayPop(b);
				ApplyBackground(idx);
			};
			_itemStrip.AddChild(b);
			b.Modulate = idx == _bgIndex ? new Color(1f, 0.92f, 0.45f) : Colors.White;
		}
	}

	/// <summary>大号物品预览按钮（贴纸缩略图 + 名称角标）</summary>
	private TextureButton MakeItemButton(string label, string texPath)
	{
		var tb = new TextureButton
		{
			CustomMinimumSize = new Vector2(150, 150),
			IgnoreTextureSize = true,
			StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
		};
		tb.TextureNormal = GD.Load<Texture2D>(texPath);
		var sb = new StyleBoxFlat
		{
			BgColor = new Color(1, 1, 1, 0.85f),
			CornerRadiusTopLeft = 22,
			CornerRadiusTopRight = 22,
			CornerRadiusBottomLeft = 22,
			CornerRadiusBottomRight = 22,
			BorderColor = new Color(0.75f, 0.75f, 0.85f),
			BorderWidthLeft = 3,
			BorderWidthRight = 3,
			BorderWidthTop = 3,
			BorderWidthBottom = 3,
			ContentMarginLeft = 10,
			ContentMarginRight = 10,
			ContentMarginTop = 10,
			ContentMarginBottom = 10,
		};
		tb.AddThemeStyleboxOverride("normal", sb);
		tb.AddThemeStyleboxOverride("hover", MakeBox(new Color(1, 1, 1, 0.95f), 22));
		tb.AddThemeStyleboxOverride("pressed", MakeBox(new Color(0.92f, 0.92f, 1.0f), 22));
		tb.AddThemeStyleboxOverride("focus", MakeBox(new Color(0, 0, 0, 0f), 22));

		var lb = new Label
		{
			Text = label,
			MouseFilter = MouseFilterEnum.Ignore,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		lb.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		lb.OffsetTop = -42;
		lb.OffsetBottom = -6;
		lb.AddThemeFontSizeOverride("font_size", 24);
		lb.AddThemeColorOverride("font_color", Colors.White);
		lb.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.7f));
		lb.AddThemeConstantOverride("outline_size", 8);
		tb.AddChild(lb);
		return tb;
	}

	private void RefreshStripHighlight()
	{
		// 重建物品栏以刷新高亮（数量少，开销可忽略）
		RebuildItemStrip();
	}

	// ================= 换装逻辑 =================

	private void ApplyTop(int index, bool silent = false)
	{
		_topIndex = index;
		var it = _tops[index];
		if (it.Tex is null)
			_top.Texture = null;
		else
		{
			_top.Texture = GD.Load<Texture2D>(it.Tex);
			_top.Scale = Vector2.One * it.Scale;
			_top.Position = it.Offset;
		}
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ApplyBottom(int index, bool silent = false)
	{
		_bottomIndex = index;
		var it = _bottoms[index];
		if (it.Tex is null)
			_bottom.Texture = null;
		else
		{
			_bottom.Texture = GD.Load<Texture2D>(it.Tex);
			_bottom.Scale = Vector2.One * it.Scale;
			_bottom.Position = it.Offset;
		}
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ApplyShoes(int index, bool silent = false)
	{
		_shoeIndex = index;
		if (index == 0)
		{
			_slipperLeft.Texture = null;
			_slipperRight.Texture = null;
		}
		else
		{
			_slipperLeft.Texture = GD.Load<Texture2D>(AssetDir + "purple_slipper_left.png");
			_slipperRight.Texture = GD.Load<Texture2D>(AssetDir + "purple_slipper_right.png");
			_slipperLeft.Scale = Vector2.One * DollScale;
			_slipperRight.Scale = Vector2.One * DollScale;
			_slipperLeft.Position = _slipperLeftOffset;
			_slipperRight.Position = _slipperRightOffset;
		}
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ApplyBackground(int index, bool silent = false)
	{
		_bgIndex = index;
		_background.Texture = _bgTextures[index];
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ToggleProp(PropItem p)
	{
		p.On = !p.On;
		if (p.Sprite != null)
			p.Sprite.Visible = p.On;
		PlayDing();
		RefreshStripHighlight();
	}

	private void EnsurePropSprites()
	{
		foreach (var p in _propItems)
		{
			var s = new Sprite2D
			{
				Texture = GD.Load<Texture2D>(AssetDir + p.Tex),
				Scale = Vector2.One * p.Scale,
				Visible = false,
			};
			_props.AddChild(s);
			p.Sprite = s;
		}
	}

	private void OnResetPressed()
	{
		PlayPop(_resetButton);
		ResetLook();
		ShowToast("已脱下所有衣服");
	}

	private void ResetLook(bool silent = false)
	{
		// 重置 = 一键脱掉所有衣服（上衣/下装/鞋子都脱下，配饰全部收起，背景保持不变）
		ApplyTop(0, silent: true);
		ApplyBottom(0, silent: true);
		ApplyShoes(0, silent: true);
		foreach (var p in _propItems)
		{
			p.On = false;
			if (p.Sprite != null)
				p.Sprite.Visible = false;
		}
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	// ================= 拍照 =================

	private void OnPhotoPressed()
	{
		PlayPop(_photoButton);
		PlayDing();
		var vp = GetViewport();
		var img = vp.GetTexture().GetImage();

		// 舞台区域：顶栏以下、底部面板以上
		float top = _topBar.OffsetTop + _topBar.Size.Y + 6;
		float bottom = Size.Y - PanelHeight + 6;
		var canvasRect = new Rect2(0, top, Size.X, bottom - top);
		var xf = vp.GetScreenTransform();
		Vector2 pxPos = xf * canvasRect.Position;
		Vector2 pxSize = xf.BasisXform(canvasRect.Size);
		var region = new Rect2I(
			(Vector2I)(pxPos + new Vector2(0.5f, 0.5f)),
			(Vector2I)(pxSize - new Vector2(1, 1)));
		region = region.Intersection(new Rect2I(Vector2I.Zero, img.GetSize()));
		GD.Print($"[Photo] img={img.GetSize()} region={region}");
		if (region.Size.X <= 8 || region.Size.Y <= 8)
		{
			GD.PushError($"[Photo] invalid capture region {region}");
			ShowToast("拍照失败");
			return;
		}

		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		_photoCounter++;
		string path = $"user://screenshots/photo_{stamp}_{_photoCounter}.png";
		var photo = img.GetRegion(region);
		var err = photo.SavePng(path);
		if (err == Error.Ok)
		{
			GD.Print($"[Photo] saved#{_photoCounter}: {ProjectSettings.GlobalizePath(path)}");
			ShowToast("咔嚓！照片已保存");
		}
		else
		{
			GD.PushError($"[Photo] save failed: {err}");
			ShowToast("保存失败");
		}
	}

	// ================= 音效与动效 =================

	private void PlayDing()
	{
		if (_sfx.Stream != null)
			_sfx.Play();
	}

	private void PlayPop(Node node)
	{
		var b = node as Control;
		if (b == null) return;
		b.PivotOffset = b.Size * 0.5f;
		var tw = CreateTween();
		tw.TweenProperty(b, "scale", new Vector2(0.9f, 0.9f), 0.06);
		tw.TweenProperty(b, "scale", Vector2.One, 0.16)
			.SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
	}

	private void ShowToast(string msg)
	{
		_toastTween?.Kill();
		_toast.Text = msg;
		_toast.Visible = true;
		_toast.Modulate = Colors.White;
		_toastTween = CreateTween();
		_toastTween.TweenInterval(1.6);
		_toastTween.TweenProperty(_toast, "modulate:a", 0f, 0.4);
		_toastTween.TweenCallback(Callable.From(() => _toast.Visible = false));
	}

	// ================= 程序化背景生成 =================

	private ImageTexture MakeBackgroundTexture(BgItem bg, int w = 720, int h = 1280)
	{
		var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
		for (int y = 0; y < h; y++)
		{
			float t = y / (float)(h - 1);
			img.FillRect(new Rect2I(0, y, w, 1), bg.Top.Lerp(bg.Bottom, t));
		}

		switch (bg.Decor)
		{
			case "stars":
				DrawStars(img, w, h);
				DrawMoon(img, (int)(w * 0.62f), (int)(h * 0.085f), (int)(w * 0.055f));
				break;
			case "sun_low":
				DrawGlowCircle(img, (int)(w * 0.22f), (int)(h * 0.17f), (int)(w * 0.15f), new Color(1f, 0.85f, 0.45f));
				break;
			case "clouds":
				DrawGlowCircle(img, (int)(w * 0.8f), (int)(h * 0.09f), (int)(w * 0.07f), new Color(1f, 0.9f, 0.55f));
				DrawCloud(img, (int)(w * 0.18f), (int)(h * 0.16f), w * 0.05f);
				DrawCloud(img, (int)(w * 0.72f), (int)(h * 0.30f), w * 0.045f);
				break;
			case "sunset":
				DrawGlowCircle(img, (int)(w * 0.75f), (int)(h * 0.22f), (int)(w * 0.12f), new Color(1f, 0.9f, 0.6f));
				break;
		}
		return ImageTexture.CreateFromImage(img);
	}

	private static void DrawStars(Image img, int w, int h)
	{
		var rng = new System.Random(20260921);
		for (int i = 0; i < 90; i++)
		{
			int x = rng.Next(0, w);
			int y = rng.Next(0, (int)(h * 0.75f));
			float bright = 0.35f + rng.NextSingle() * 0.6f;
			var c = new Color(1, 1, 1, bright);
			img.FillRect(new Rect2I(x, y, 2, 2), c);
			if (rng.NextSingle() > 0.7f)
			{
				img.FillRect(new Rect2I(x - 1, y, 4, 1), new Color(1, 1, 1, bright * 0.5f));
				img.FillRect(new Rect2I(x, y - 1, 1, 4), new Color(1, 1, 1, bright * 0.5f));
			}
		}
	}

	private static void DrawMoon(Image img, int cx, int cy, int r)
	{
		for (int dy = -r; dy <= r; dy++)
		{
			int dx = (int)Mathf.Sqrt(r * r - dy * dy);
			img.FillRect(new Rect2I(cx - dx, cy + dy, dx * 2, 1), new Color(1f, 0.97f, 0.82f));
		}
		// 月牙：用背景色圆偏移覆盖
		for (int dy = -r; dy <= r; dy++)
		{
			int dx = (int)Mathf.Sqrt(r * r - dy * dy);
			img.FillRect(new Rect2I(cx - dx + r / 2, cy + dy - r / 5, dx * 2, 1), new Color(0, 0, 0, 0));
		}
		// 用半透明暗圆制造月牙阴影
		for (int dy = -r; dy <= r; dy++)
		{
			int dx = (int)Mathf.Sqrt(r * r - dy * dy);
			int offX = r / 3;
			int offY = -r / 5;
			int y = cy + dy + offY;
			if (y < 0 || y >= img.GetHeight()) continue;
			img.FillRect(new Rect2I(cx - dx + offX, y, dx * 2, 1), new Color(0.1f, 0.12f, 0.3f, 0.85f));
		}
	}

	private static void DrawGlowCircle(Image img, int cx, int cy, int r, Color core)
	{
		for (int dy = -r * 2; dy <= r * 2; dy++)
		{
			float dist = Mathf.Abs(dy);
			float a = Mathf.Clamp(1f - dist / (2f * r), 0f, 1f);
			if (a <= 0f) continue;
			int half = (int)(r * Mathf.Sqrt(Mathf.Max(0f, 1f - (dist / (2f * r)) * (dist / (2f * r)) * 4f)));
			var c = new Color(core.R, core.G, core.B, a * 0.9f);
			img.FillRect(new Rect2I(cx - half, cy + dy, half * 2, 1), c);
		}
	}

	private static void DrawCloud(Image img, int cx, int cy, float r)
	{
		var white = new Color(1, 1, 1, 0.85f);
		int ir = (int)r;
		void Puff(int ox, int oy, int rr)
		{
			for (int dy = -rr; dy <= rr; dy++)
			{
				int dx = (int)Mathf.Sqrt(rr * rr - dy * dy);
				img.FillRect(new Rect2I(cx + ox - dx, cy + oy + dy, dx * 2, 1), white);
			}
		}
		Puff(-ir, 0, (int)(r * 0.7f));
		Puff(ir, 0, (int)(r * 0.7f));
		Puff(0, -ir / 3, (int)(r * 0.95f));
	}

	// ================= 自测模式 =================

	private async Task RunSelfTestAsync()
	{
		GD.Print("[SELFTEST] begin");
		await Wait(0.4);

		// 校验纹理加载
		int nullTex = 0;
		if (_doll.Texture == null) { GD.PushError("[SELFTEST] doll texture null"); nullTex++; }
		foreach (var p in _propItems)
			if (p.Sprite?.Texture == null) { GD.PushError($"[SELFTEST] prop {p.Label} texture null"); nullTex++; }
		if (_sfx.Stream == null) { GD.PushError("[SELFTEST] ding stream null"); nullTex++; }

		// 逐分类切换
		for (int i = 0; i < _tops.Count; i++) { ApplyTop(i); await Wait(0.12); }
		for (int i = 0; i < _bottoms.Count; i++) { ApplyBottom(i); await Wait(0.12); }
		for (int i = 0; i < 2; i++) { ApplyShoes(i); await Wait(0.12); }
		for (int i = 0; i < _bgs.Count; i++) { ApplyBackground(i); await Wait(0.12); }

		// 配饰全开
		foreach (var p in _propItems) { ToggleProp(p); await Wait(0.06); }
		await Wait(0.3);

		// 拍照
		OnPhotoPressed();
		await Wait(0.8);

		// 重置后拍初始造型（睡衣全套+夜晚背景）
		ResetLook();
		await Wait(0.4);
		GD.Print($"[SELFTEST] state after reset: top={_topIndex} bottom={_bottomIndex} shoes={_shoeIndex} bg={_bgIndex}");
		OnPhotoPressed();
		await Wait(0.8);

		// 验证截图文件
		string dir = ProjectSettings.GlobalizePath("user://screenshots");
		bool found = System.IO.Directory.Exists(dir) &&
					 System.IO.Directory.GetFiles(dir, "photo_*.png").Length > 0;
		GD.Print($"[SELFTEST] photo exists: {found}");
		GD.Print(found && nullTex == 0 ? "[SELFTEST] PASSED" : "[SELFTEST] FAILED");
		await Wait(0.5);
		GetTree().Quit();
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}
}
