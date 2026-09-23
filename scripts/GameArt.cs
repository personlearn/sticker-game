#nullable enable
using Godot;

/// <summary>
/// 三个场景（首页 / 贴纸游戏 / 雷霆战机）共用的「美术 + UI 小工具」。
///
/// 这里所有图形都是**纯代码画**的，不需要任何素材文件：
/// 用 CanvasItem 的 _Draw + DrawXxx 系列（GPU 侧绘制）。
/// 这和贴纸游戏里「用 Image 逐像素 FillRect 生成背景」是两条完全不同的路子——
/// 逐像素那套要自己算混合（FillRect 不做 alpha 混合），而 DrawXxx 画的半透明颜色
/// 是真正参与混合的，所以这里可以放心写 alpha。
/// </summary>
public static class GameArt
{
	// ---------- 主题 ----------

	/// <summary>
	/// 中文字体。<see cref="MakeUiTheme"/> 会顺手把它缓存下来——
	/// <c>_Draw</c> 里想画文字（比如羊了个羊牌堆上那个「下面还藏着几张」的角标）
	/// 需要一个 <see cref="Font"/>，而那时手上没有 Theme 可用。
	/// </summary>
	public static Font? UiFont { get; private set; }

	/// <summary>
	/// 全局主题：指定中文字体，否则 Godot 自带字体显示中文会是方块。
	/// 和贴纸游戏里的 BuildTheme() 是同一套逻辑，抽出来给所有场景共用。
	/// </summary>
	public static Theme MakeUiTheme(int defaultSize = 32)
	{
		var theme = new Theme();
		var font = new SystemFont
		{
			FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC", "sans-serif" },
		};
		UiFont = font;
		theme.DefaultFont = font;
		theme.DefaultFontSize = defaultSize;
		return theme;
	}

	// ---------- 样式 ----------

	public static StyleBoxFlat MakeBox(Color bg, float radius = 18, Color? border = null)
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

	/// <summary>给按钮套上一整套配色（normal / hover / pressed / 文字四态）。</summary>
	public static void StyleButton(Button b, Color bg, Color font, float radius = 18,
		Color? border = null, int fontSize = 34)
	{
		b.AddThemeStyleboxOverride("normal", MakeBox(bg, radius, border));
		b.AddThemeStyleboxOverride("hover", MakeBox(bg.Lightened(0.12f), radius, border));
		b.AddThemeStyleboxOverride("pressed", MakeBox(bg.Darkened(0.18f), radius, border));
		b.AddThemeColorOverride("font_color", font);
		b.AddThemeColorOverride("font_hover_color", font);
		b.AddThemeColorOverride("font_pressed_color", font);
		b.AddThemeColorOverride("font_focus_color", font);
		b.AddThemeFontSizeOverride("font_size", fontSize);
		b.PivotOffset = new Vector2(0.5f, 0.5f);
	}

	/// <summary>给 Label 加上「白字 + 深色描边」，保证压在花花绿绿的背景上也读得清。</summary>
	public static void OutlineText(Label lb, Color color, int fontSize, int outline = 6)
	{
		lb.AddThemeFontSizeOverride("font_size", fontSize);
		lb.AddThemeColorOverride("font_color", color);
		lb.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.6f));
		lb.AddThemeConstantOverride("outline_size", outline);
	}

	// ---------- 渐变背景 ----------

	/// <summary>
	/// 竖直渐变纹理。
	/// 贴纸游戏是拿 Image 逐行 FillRect 画出 720×1280 的渐变（14 MB 内存 + 5120 次跨边界调用），
	/// 这里用引擎内置的 GradientTexture2D：完全在 GPU 侧，开销可以忽略。
	/// </summary>
	public static GradientTexture2D VerticalGradient(Color top, Color bottom)
	{
		var g = new Gradient();
		g.SetColor(0, top);
		g.SetColor(1, bottom);
		return new GradientTexture2D
		{
			Gradient = g,
			Width = 8,
			Height = 256,
			Fill = GradientTexture2D.FillEnum.Linear,
			FillFrom = new Vector2(0.5f, 0f),
			FillTo = new Vector2(0.5f, 1f),
		};
	}

	// ---------- 自测辅助 ----------

	/// <summary>
	/// 这个像素是不是「视口清屏色」（Godot 默认 0.3 灰，本项目没改过）。
	/// 用途：背景没铺满时会露出清屏色，用它就能断言「背景真的盖住了整屏」。
	/// 这个坑真的踩过：GradientTexture2D 默认很小（我们设的 8×256），
	/// 而 TextureRect 的最小尺寸跟着纹理走，于是背景只在左上角画了一小块，其余全是灰的。
	/// </summary>
	public static bool IsClearColor(Color c)
	{
		const float g = 0.3f;
		return Mathf.Abs(c.R - g) < 0.02f && Mathf.Abs(c.G - g) < 0.02f && Mathf.Abs(c.B - g) < 0.02f;
	}

	// ---------- 图形 ----------
	/// <summary>填一个多边形，再描一圈边（描边要自己把首点接到末尾，否则最后一条边是缺的）。</summary>
	public static void Poly(CanvasItem ci, Vector2[] pts, Color fill, Color line, float lineWidth = 2.5f)
	{
		ci.DrawColoredPolygon(pts, fill);
		if (lineWidth <= 0f)
			return;
		var closed = new Vector2[pts.Length + 1];
		System.Array.Copy(pts, closed, pts.Length);
		closed[^1] = pts[0];
		ci.DrawPolyline(closed, line, lineWidth, true);
	}

	/// <summary>
	/// 自机（战机）。local 坐标：机头朝上（-y），整体约 62×80。
	/// flame 是尾焰长度系数（0~1.4），由游戏每帧摇晃着传进来。
	/// </summary>
	public static void DrawShip(CanvasItem ci, float scale, float flame)
	{
		var hull = new Color("#8fe9ff");
		var wing = new Color("#58c4e8");
		var line = new Color("#1d6f8c");

		Vector2[] P(params float[] xy)
		{
			var a = new Vector2[xy.Length / 2];
			for (int i = 0; i < a.Length; i++)
				a[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]) * scale;
			return a;
		}

		// 尾焰：外焰 + 内焰（画在最下层）
		if (flame > 0f)
		{
			ci.DrawColoredPolygon(P(7, 32, -7, 32, 0, 32 + 26 * flame), new Color("#ff9a3c"));
			ci.DrawColoredPolygon(P(4, 32, -4, 32, 0, 32 + 15 * flame), new Color("#ffe08a"));
		}

		// 尾翼
		Poly(ci, P(7, 18, 15, 34, 2, 34), wing, line, 2f * scale);
		Poly(ci, P(-7, 18, -15, 34, -2, 34), wing, line, 2f * scale);
		// 机翼
		Poly(ci, P(8, -4, 30, 20, 30, 29, 8, 20), wing, line, 2f * scale);
		Poly(ci, P(-8, -4, -30, 20, -30, 29, -8, 20), wing, line, 2f * scale);
		// 机身
		Poly(ci, P(0, -40, 11, -6, 8, 26, -8, 26, -11, -6), hull, line, 2.5f * scale);
		// 座舱
		ci.DrawCircle(new Vector2(0, -16) * scale, 6.5f * scale, new Color("#e8fbff"));
		ci.DrawArc(new Vector2(0, -16) * scale, 6.5f * scale, 0, Mathf.Tau, 16, line, 1.5f * scale, true);
	}

	/// <summary>小敌机：直冲型。倒三角，约 40×38。</summary>
	public static void DrawScout(CanvasItem ci)
	{
		Poly(ci, new[] { new Vector2(0, 22), new Vector2(20, -16), new Vector2(-20, -16) },
			new Color("#ff8a5c"), new Color("#8a3210"), 2.5f);
		Poly(ci, new[] { new Vector2(0, 10), new Vector2(9, -8), new Vector2(-9, -8) },
			new Color("#ffd9c2"), new Color("#8a3210"), 2f);
	}

	/// <summary>蛇形敌机：六边形，约 44×42。</summary>
	public static void DrawWeaver(CanvasItem ci)
	{
		Poly(ci, new[]
		{
			new Vector2(0, 24), new Vector2(22, 5), new Vector2(16, -18),
			new Vector2(-16, -18), new Vector2(-22, 5),
		}, new Color("#b98cff"), new Color("#4a2a8a"), 2.5f);
		ci.DrawCircle(Vector2.Zero, 8f, new Color("#efe6ff"));
	}

	/// <summary>炮台敌机：会悬停射击。约 52×48。</summary>
	public static void DrawGunner(CanvasItem ci)
	{
		var body = new Color("#ff5c6e");
		var line = new Color("#7a1524");
		Poly(ci, new[] { new Vector2(-22, 18), new Vector2(22, 18), new Vector2(26, -16), new Vector2(-26, -16) },
			body, line, 2.5f);
		ci.DrawRect(new Rect2(-23, 16, 8, 14), new Color("#ffd0d6"));
		ci.DrawRect(new Rect2(15, 16, 8, 14), new Color("#ffd0d6"));
		ci.DrawCircle(new Vector2(0, 0), 9f, new Color("#ffe3e6"));
		ci.DrawArc(new Vector2(0, 0), 9f, 0, Mathf.Tau, 20, line, 2f, true);
	}

	/// <summary>火力升级道具：绿色六边形 + 白色核心。</summary>
	public static void DrawPickup(CanvasItem ci)
	{
		Poly(ci, new[]
		{
			new Vector2(0, 18), new Vector2(16, 9), new Vector2(16, -9),
			new Vector2(0, -18), new Vector2(-16, -9), new Vector2(-16, 9),
		}, new Color("#9be86a"), new Color("#2f6b12"), 2.5f);
		ci.DrawCircle(Vector2.Zero, 6f, Colors.White);
		ci.DrawArc(Vector2.Zero, 6f, 0, Mathf.Tau, 16, new Color("#2f6b12"), 1.5f, true);
	}

	/// <summary>
	/// 椭圆（DrawCircle 只能画正圆，所以自己按角度采样成多边形）。
	/// 描边宽度为 0 时只填不描。
	/// </summary>
	public static void Ellipse(CanvasItem ci, Vector2 center, float rx, float ry, Color fill,
		Color line = default, float lineWidth = 0f, int segments = 28)
	{
		var pts = new Vector2[segments];
		for (int i = 0; i < segments; i++)
		{
			float a = Mathf.Tau * i / segments;
			pts[i] = center + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
		}
		ci.DrawColoredPolygon(pts, fill);
		if (lineWidth > 0f)
		{
			var closed = new Vector2[segments + 1];
			System.Array.Copy(pts, closed, segments);
			closed[^1] = pts[0];
			ci.DrawPolyline(closed, line, lineWidth, true);
		}
	}

	/// <summary>
	/// 地雷（扫雷用）。<paramref name="r"/> 是球体半径，8 根刺会伸到约 1.5r；
	/// <paramref name="center"/> 是球心，默认 (0,0)（Node2D 自己画的场合就够用）。
	///
	/// 深蓝棋盘上要够亮才看得见，所以球体用近白的冷色 + 深蓝描边——
	/// 传统扫雷那种纯黑地雷压在深色格子上就是一团看不见的黑。
	/// </summary>
	public static void DrawMine(CanvasItem ci, float r, Vector2 center = default)
	{
		var ink = new Color("#141b33");     // 球体描边：把球和刺分开
		var spike = new Color("#9fb0d8");   // 刺要够亮，否则压在深色格子上就看不出来了
		var body = new Color("#eef3ff");

		// 8 根刺先画，压在球体下面（露出来的那一截才是刺）。
		// 刺不能太长：伸出球体太多就成了一颗太阳，短而粗才是地雷的剪影。
		for (int i = 0; i < 8; i++)
		{
			float a = Mathf.Tau * i / 8f;
			var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
			ci.DrawLine(center + dir * (r * 0.5f), center + dir * (r * 1.18f), spike, r * 0.24f, true);
		}

		ci.DrawCircle(center, r, ink);                                        // 描边
		ci.DrawCircle(center, r * 0.84f, body);                               // 球体
		ci.DrawCircle(center + new Vector2(-r * 0.17f, -r * 0.19f), r * 0.42f, Colors.White); // 高光
	}

	/// <summary>
	/// 旗子（扫雷用）。<paramref name="r"/> 约等于旗子宽度的一半；
	/// 形状整体偏上（旗杆底在 r*0.75 处），这样视觉重心正好落在格子中间。
	/// </summary>
	public static void DrawFlag(CanvasItem ci, float r, Vector2 center = default)
	{
		var pole = new Color("#e8eeff");
		var cloth = new Color("#ff6b81");
		var line = new Color("#8c1f33");

		// 旗杆（从旗面顶端一直插到底座）
		ci.DrawLine(center + new Vector2(-r * 0.05f, -r * 0.66f),
			center + new Vector2(-r * 0.05f, r * 0.66f), pole, r * 0.16f, true);

		// 底座：一个梯形，让旗子看起来是「立」着的
		ci.DrawColoredPolygon(new[]
		{
			center + new Vector2(-r * 0.52f, r * 0.72f),
			center + new Vector2(r * 0.44f, r * 0.72f),
			center + new Vector2(r * 0.28f, r * 0.48f),
			center + new Vector2(-r * 0.36f, r * 0.48f),
		}, pole);

		// 旗面：三角
		Poly(ci, new[]
		{
			center + new Vector2(-r * 0.02f, -r * 0.66f),
			center + new Vector2(r * 0.88f, -r * 0.26f),
			center + new Vector2(-r * 0.02f, r * 0.14f),
		}, cloth, line, r * 0.11f);
	}

	/// <summary>
	/// 扑克花色（蜘蛛纸牌用）。<paramref name="suit"/>：0 黑桃 / 1 红桃 / 2 梅花 / 3 方块。
	/// <paramref name="s"/> 是「半个字高」——整朵花大约 2s 高、1.3s 宽。<paramref name="c"/> 是花朵中心。
	///
	/// 为什么不直接画 ♠♥♣♦ 这几个字符：那要指望系统字体里恰好有 U+2660..2667 这几个码位，
	/// 而这个项目一向是「零素材、图形全部画出来」，矢量画法在任何机器上长得都一样。
	/// 构造方式：红桃 = 两个圆 + 一个倒三角；黑桃 = 红桃上下翻转 + 一个梯形柄；梅花 = 三个圆 + 柄；方块 = 菱形。
	/// </summary>
	public static void DrawSuit(CanvasItem ci, int suit, Vector2 c, float s, Color color)
	{
		switch (suit)
		{
			case 0: // 黑桃
				ci.DrawCircle(c + new Vector2(-s * 0.30f, s * 0.18f), s * 0.36f, color);
				ci.DrawCircle(c + new Vector2(s * 0.30f, s * 0.18f), s * 0.36f, color);
				Fill(ci, c + new Vector2(-s * 0.62f, s * 0.16f), c + new Vector2(s * 0.62f, s * 0.16f),
					c + new Vector2(0f, -s * 0.80f), color);
				Fill(ci, c + new Vector2(-s * 0.26f, s * 0.40f), c + new Vector2(s * 0.26f, s * 0.40f),
					c + new Vector2(s * 0.11f, s * 0.82f), c + new Vector2(-s * 0.11f, s * 0.82f), color);
				break;

			case 1: // 红桃
				ci.DrawCircle(c + new Vector2(-s * 0.30f, -s * 0.18f), s * 0.36f, color);
				ci.DrawCircle(c + new Vector2(s * 0.30f, -s * 0.18f), s * 0.36f, color);
				Fill(ci, c + new Vector2(-s * 0.62f, -s * 0.14f), c + new Vector2(s * 0.62f, -s * 0.14f),
					c + new Vector2(0f, s * 0.82f), color);
				break;

			case 2: // 梅花
				ci.DrawCircle(c + new Vector2(0f, -s * 0.34f), s * 0.34f, color);
				ci.DrawCircle(c + new Vector2(-s * 0.36f, s * 0.14f), s * 0.34f, color);
				ci.DrawCircle(c + new Vector2(s * 0.36f, s * 0.14f), s * 0.34f, color);
				Fill(ci, c + new Vector2(-s * 0.26f, s * 0.30f), c + new Vector2(s * 0.26f, s * 0.30f),
					c + new Vector2(s * 0.11f, s * 0.82f), c + new Vector2(-s * 0.11f, s * 0.82f), color);
				break;

			default: // 方块
				Fill(ci, c + new Vector2(0f, -s * 0.82f), c + new Vector2(s * 0.58f, 0f),
					c + new Vector2(0f, s * 0.82f), c + new Vector2(-s * 0.58f, 0f), color);
				break;
		}
	}

	/// <summary>只填不描边的三角形 / 四边形（花色的零件用，描边会把零件之间的接缝画出来）。</summary>
	private static void Fill(CanvasItem ci, Vector2 a, Vector2 b, Vector2 c, Color color)
		=> ci.DrawColoredPolygon(new[] { a, b, c }, color);

	private static void Fill(CanvasItem ci, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
		=> ci.DrawColoredPolygon(new[] { a, b, c, d }, color);

	/// <summary>
	/// 蜘蛛（蜘蛛纸牌的图标）。<paramref name="r"/> 是身体半径，八条腿会伸到约 1.7r 外。
	/// 八条腿是影射游戏规则：八组 K→A 收齐才算赢。
	/// </summary>
	public static void DrawSpider(CanvasItem ci, Vector2 c, float r, Color body, Color line)
	{
		// 腿先画，压在身体下面。每侧四条，两条折线组成一个「膝」。
		for (int i = 0; i < 4; i++)
		{
			float y0 = -r * 0.62f + i * r * 0.42f;
			float reach = 1.35f + i * 0.18f;
			for (int sgn = -1; sgn <= 1; sgn += 2)
			{
				var root = c + new Vector2(sgn * r * 0.42f, y0);
				var knee = c + new Vector2(sgn * r * 0.95f, y0 - r * 0.52f);
				var foot = c + new Vector2(sgn * r * reach, y0 + r * 0.60f);
				ci.DrawLine(root, knee, line, r * 0.14f, true);
				ci.DrawLine(knee, foot, line, r * 0.11f, true);
			}
		}

		// 身体：一个大腹 + 一个小头 + 两颗眼
		Ellipse(ci, c + new Vector2(0f, r * 0.22f), r * 0.62f, r * 0.72f, body, line, r * 0.08f);
		ci.DrawCircle(c + new Vector2(0f, -r * 0.62f), r * 0.36f, body);
		ci.DrawArc(c + new Vector2(0f, -r * 0.62f), r * 0.36f, 0, Mathf.Tau, 18, line, r * 0.08f, true);
		ci.DrawCircle(c + new Vector2(-r * 0.14f, -r * 0.68f), r * 0.085f, line);
		ci.DrawCircle(c + new Vector2(r * 0.14f, -r * 0.68f), r * 0.085f, line);
	}

	/// <summary>
	/// 羊头（首页「羊了个羊」卡片上的图标，local 原点在羊头中心，整体约 100×100）。
	/// scale=1 时半径约 50px；外面传 0.95~1.0 做轻微的呼吸动画。
	/// </summary>
	public static void DrawSheepHead(CanvasItem ci, float scale)
	{
		float r = 50f * scale;
		var wool = new Color("#fff9ef");
		var woolLine = new Color("#c6bba8");
		var face = new Color("#6f5847");
		var faceLine = new Color("#3d2f25");
		var eye = new Color("#2b2320");

		// 一圈羊毛：6 个白色小球围成一圈，看起来毛茸茸
		for (int i = 0; i < 6; i++)
		{
			float a = Mathf.Tau * i / 6f - Mathf.Pi * 0.5f;
			var c = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * 0.62f;
			ci.DrawCircle(c, r * 0.44f, wool);
			ci.DrawArc(c, r * 0.44f, 0, Mathf.Tau, 20, woolLine, r * 0.055f, true);
		}
		ci.DrawCircle(Vector2.Zero, r * 0.7f, wool);

		// 耳朵：两侧的小椭圆（先画，压在脸下面）
		Ellipse(ci, new Vector2(-r * 0.66f, r * 0.06f), r * 0.26f, r * 0.15f, face, faceLine, r * 0.05f);
		Ellipse(ci, new Vector2(r * 0.66f, r * 0.06f), r * 0.26f, r * 0.15f, face, faceLine, r * 0.05f);

		// 脸
		Ellipse(ci, new Vector2(0f, r * 0.16f), r * 0.48f, r * 0.42f, face, faceLine, r * 0.06f);

		// 眼睛（两颗黑豆）+ 鼻子
		ci.DrawCircle(new Vector2(-r * 0.19f, r * 0.06f), r * 0.085f, eye);
		ci.DrawCircle(new Vector2(r * 0.19f, r * 0.06f), r * 0.085f, eye);
		ci.DrawCircle(new Vector2(0f, r * 0.34f), r * 0.08f, faceLine);
		ci.DrawArc(new Vector2(0f, r * 0.3f), r * 0.16f, 0.25f * Mathf.Pi, 0.75f * Mathf.Pi,
			12, faceLine, r * 0.045f, true);

		// 脑门上的一撮毛
		ci.DrawCircle(new Vector2(0f, -r * 0.6f), r * 0.28f, wool);
		ci.DrawArc(new Vector2(0f, -r * 0.6f), r * 0.28f, 0, Mathf.Tau, 18, woolLine, r * 0.05f, true);
	}
}
