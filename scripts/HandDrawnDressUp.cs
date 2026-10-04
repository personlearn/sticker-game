#nullable enable
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 手绘换装游戏（贴纸游戏选择页里的「手绘换装」）。
///
/// 素材是孩子手绘的一整套贴纸（<c>assset/hand-drawn-dress-up/</c>），分两批照片陆续加进来：
/// 一个小女孩线稿 + 六条裙子 + 三个发饰。玩法比 <see cref="StickerGame"/> 简单些：
/// 裙子 / 发箍都是单选，另外给了一组程序化生成的背景，外加拍照留念。
///
/// 渲染分层（绝对 z_index）：背景(-10) &lt; 娃娃线稿(0) &lt; 裙子(10) &lt; 发箍(20) &lt; 装饰贴纸(30)。
///
/// 摆放的关键：裙子/发箍的手绘比例和娃娃线稿并不一致（孩子画的裙子是「窄领口 + 大喇叭裙摆」的
/// 三角形，娃娃的身子却是窄梯形），光靠等比缩放怎么摆都不像「穿在身上」。
/// 所以每件部件都用一次**梯形拉伸**贴上去：上下两条边各自有自己的中心与宽度，
/// 中间按高度线性过渡——参数见下面的数据表，拉伸本身由 <see cref="FlareShader"/> 完成。
/// </summary>
public partial class HandDrawnDressUp : Control
{
	// ---------- 分层 ZIndex 常量（绝对层号）----------
	private const int ZDress = 10;
	private const int ZHeadband = 20;
	private const int ZDeco = 30;          // 装饰贴纸压在最上面
	private const int ZDecoFrame = 31;     // 选中的贴纸外框（再高一档）

	// ---------- 分类标签下标 ----------
	private const int CatDeco = 6;         // 「装饰」标签（见 BuildCategoryTabs 的顺序）

	// ---------- 初始造型（开局和「重置」都回到这里）----------
	private const int InitialDress = 1;    // 蓝心裙
	private const int InitialHeadband = 0; // 不戴发箍
	private const int InitialHair = 0;     // 原色
	private const int InitialLip = 0;      // 无唇彩
	private const int InitialSkin = 0;     // 冷白
	private const int InitialSock = 0;     // 原色
	private const int InitialBg = 0;       // 画纸

	/// <summary>
	/// 梯形拉伸的着色器：把一块矩形贴图「上窄下宽（或上宽下窄）」地抻开。
	///
	/// 逐像素反查：目标像素落在哪一行（p.y）决定这一行的缩放/中心，
	/// 再按 (p.x - 中心) / 宽度 反算回源的 u。这样上沿、下沿的宽度可以各自独立设，
	/// 等比缩放做不到的事（领口贴合身子、裙摆又收得住）就能做到了。
	/// </summary>
	private const string FlareShader = @"
shader_type canvas_item;

uniform vec2 uv_min = vec2(0.0);   // 这件衣服在贴图里真正要用的范围（归一化）
uniform vec2 uv_max = vec2(1.0);
uniform float cen_top = 0.5;       // 上沿中心（按目标包围盒宽度归一化）
uniform float cen_bot = 0.5;       // 下沿中心
uniform float span_top = 1.0;      // 上沿宽度（同上）
uniform float span_bot = 1.0;      // 下沿宽度

void fragment() {
	vec2 p = (UV - uv_min) / (uv_max - uv_min);   // 源裁切范围内的 0~1
	float w = mix(span_top, span_bot, p.y);
	float c = mix(cen_top, cen_bot, p.y);
	float su = 0.5 + (p.x - c) / max(w, 0.0001);
	// 落在源矩形外（或拉伸后超出源行宽）的像素直接留空
	bool inside = p.x >= 0.0 && p.x <= 1.0 && p.y >= 0.0 && p.y <= 1.0 && su >= 0.0 && su <= 1.0;
	if (inside) {
		COLOR = texture(TEXTURE, mix(uv_min, uv_max, vec2(su, p.y))) * COLOR;
	} else {
		COLOR = vec4(0.0);
	}
}";

	/// <summary>
	/// 娃娃线稿的「涂色」着色器：按区域掩码给娃娃上色。
	///
	/// 掩码通道：R=肤色、G=头发、B=唇彩、A=袜子（RGB 三通道用满了，袜子挪到 alpha 上；
	/// 这张掩码只当普通 uniform 贴图读，alpha 不参与输出混合，所以拿它当第四个区域是安全的）。
	///
	/// 线稿所有区域都是纸白，光看颜色分不出哪块是头发哪块是脸，所以掩码由 C# 侧泛洪算出
	/// （见 <see cref="BuildRegionMap"/>），这里只负责按掩码换色。
	///
	/// 配色用的是「涂色书」思路：亮的地方（填色）上目标色，暗的地方（铅笔线）保留成暗色，
	/// 而不是简单相乘——否则染深色时线稿会整块糊掉。
	/// tint 越接近白色就越退化成「原样乘以 tint」，所以选「原色」时画面和没染一样。
	/// </summary>
	private const string DollTintShader = @"
shader_type canvas_item;

uniform sampler2D region_map : filter_nearest;
uniform vec3 hair_tint = vec3(1.0);
uniform vec3 lip_tint = vec3(1.0);
uniform vec3 skin_tint = vec3(1.0);
uniform vec3 sock_tint = vec3(1.0);

void fragment() {
	vec4 base = texture(TEXTURE, UV) * COLOR;
	vec4 m4 = texture(region_map, UV);
	vec3 m = m4.rgb;
	vec3 tint = vec3(1.0);
	if (m.g > 0.5) { tint = hair_tint; }
	if (m.b > 0.5) { tint = lip_tint; }
	if (m.r > 0.5) { tint = skin_tint; }
	if (m4.a > 0.5) { tint = sock_tint; }

	float mask = max(max(max(m.r, m.g), m.b), m4.a);
	if (mask > 0.5) {
		float lum = dot(base.rgb, vec3(0.299, 0.587, 0.114));
		float tint_lum = dot(tint, vec3(0.299, 0.587, 0.114));
		// 直接相乘：保持线稿明暗，但染深色时线会糊掉
		vec3 mul = base.rgb * tint;
		// 保线稿：亮处上目标色、暗处留一条暗线
		float t = smoothstep(0.55, 0.95, lum);
		vec3 keep = mix(vec3(0.25 + 0.40 * tint_lum), tint, t);
		base.rgb = mix(mul, keep, 1.0 - tint_lum);
	}
	COLOR = base;
}";

	private const string AssetDir = "res://assset/hand-drawn-dress-up/";
	private const string DingPath = "res://sfx/ding.wav";

	private const float DollScale = 0.92f;
	// 面板比最初高一截：多了一行「旋转」（48 + 12 间距），所以加 60
	private const float PanelHeight = 500f;

	// 裙子和发箍可缩放的范围
	private const float MinUserScale = 0.5f;
	private const float MaxUserScale = 2.0f;

	// 装饰贴纸：贴图边长 256，1.0 倍时在画布上约占 DecoBaseSize（娃娃贴图像素）
	private const int DecoTextureSize = 256;
	private const float DecoBaseSize = 120f;
	private const float DecoMinRot = -180f;
	private const float DecoMaxRot = 180f;
	private const int MaxStickers = 40;    // 贴纸上限，防止一路点下去把帧数拖垮
	private const int DecoSuperSample = 3; // 贴图先在 3 倍大的画布上硬边作画，再缩回来，边缘就自带抗锯齿

	// 卡通贴纸的「粗黑边」。所有形状都是「先画一圈放大的描边色、再画本体」画出来的。
	private static readonly Color DecoInk = new("#3a3a48");

	// Toast 提示的顶边（画布坐标）。顶栏下沿 y=112，娃娃头顶 y≈212，提示摆在这条空档里。
	private const float ToastTop = 118f;

	private static readonly Color HighlightTint = new Color(1f, 0.92f, 0.45f);

	// ---------- 场景节点 ----------
	private TextureRect _background = null!;
	private Node2D _character = null!;
	private Sprite2D _doll = null!;
	private Sprite2D _dress = null!;
	private Sprite2D _headband = null!;
	private HBoxContainer _topBar = null!;
	private HBoxContainer _categoryTabs = null!;
	private ScrollContainer _itemScroll = null!;
	private HBoxContainer _itemStrip = null!;
	private StripPager _stripPager = null!;
	private Button _homeButton = null!;
	private Button _resetButton = null!;
	private Button _photoButton = null!;
	private HBoxContainer _scaleRow = null!;
	private HSlider _scaleSlider = null!;
	private Label _scaleValue = null!;
	private AudioStreamPlayer _sfx = null!;
	private Label _toast = null!;

	// ---------- 部件数据模型 ----------

	/// <summary>
	/// 穿在身上的部件（裙子 / 发箍）：单选。
	///
	/// 为什么不是「等比缩放 + 偏移」：孩子画的裙子是「窄领口 + 大喇叭裙摆」的三角形，
	/// 而娃娃的身子是个窄梯形——一个等比缩放不可能同时对上领口和裙摆
	/// （对上裙摆领口就细成一根带子，对上领口裙摆就撑成一顶帐篷）。
	/// 所以这里描述的是一次**梯形拉伸**：上下两条边各自有自己的中心与宽度，
	/// 中间按高度线性过渡（见 <see cref="FlareShader"/>）。这样就能把裙子「抻」成贴合娃娃的形状。
	///
	/// 坐标系：所有几何量都是「娃娃贴图像素」，原点在娃娃图片中心（和娃娃线稿同一套单位）。
	/// </summary>
	private sealed class WearItem
	{
		public string Label = "";
		public string? Tex;          // null = 不穿 / 不戴

		/// <summary>贴图里真正要用的范围（像素）。竖直方向：它的上/下边正好落到 TopY / BotY。</summary>
		public Rect2 Src;

		public float TopY;           // 目标上沿（领口 / 发箍拱顶）
		public float BotY;           // 目标下沿（裙摆 / 发箍飘带末端）
		public float TopCenterX;     // 上沿中心
		public float TopWidth;       // 上沿宽度
		public float BotCenterX;     // 下沿中心
		public float BotWidth;       // 下沿宽度

		// ---- 玩家调出来的状态（「重置」时清回去）----
		public float UserScale = 1f; // 「大小」滑块：0.5 ~ 2
		public Vector2 Drag;         // 拖动的位移（娃娃贴图像素）
	}

	/// <summary>可切换的颜色（头发 / 唇彩 / 肤色 / 袜子共用）。Tone 就是着色器里的 tint。</summary>
	private sealed class ColorItem
	{
		public string Label = "";
		public Color Tone = Colors.White;
	}

	/// <summary>
	/// 一种装饰贴纸（蝴蝶结 / 星星 / 爱心 …）。
	///
	/// 素材里没有这些手绘件，所以贴图是代码画的（见 <see cref="BuildDecoTextures"/>）：
	/// 先在放大若干倍的画布上按形状方程填色，再缩回 <see cref="DecoTextureSize"/>，
	/// 边缘就自带抗锯齿。Mask 是「那一像素实不实」的布尔表，拖动时按它判定命中。
	/// </summary>
	private sealed class DecoItem
	{
		public string Label = "";
		public ImageTexture Tex = null!;
		public bool[] Mask = System.Array.Empty<bool>();
	}

	/// <summary>
	/// 已经贴到画布上的一个贴纸。位置用「娃娃贴图像素」（和穿戴物同一套坐标，
	/// 原点在娃娃图片中心），这样窗口缩放时能跟着娃娃一起走。
	/// </summary>
	private sealed class Sticker
	{
		public int Type;
		public Sprite2D Node = null!;
		public Vector2 Pos;
		public float UserScale = 1f;
		public float Deg;             // 旋转角度（度）
	}

	/// <summary>新贴纸的落点（娃娃贴图像素）：轮流用，免得连点两下叠在一起。</summary>
	private static readonly Vector2[] DecoSpots =
	{
		new(-72f, -150f),  // 头顶左边
		new(72f, -158f),   // 头顶右边
		new(0f, -58f),     // 领口
		new(-82f, 42f),    // 腰左
		new(82f, 42f),     // 腰右
		new(0f, 150f),     // 裙摆下
	};

	// ---------------------------------------------------------------------------
	// 娃娃线稿的区域掩码：这些点由 .workbuddy/tools/regions.ps1 分块标色后选出，
	// 每个点都落在对应区域内部（娃娃贴图像素，原点在图片左上角）。
	// 光靠颜色分不出「哪块是头发、哪块是脸」——线稿所有填色都是纸白——所以只能靠泛洪划区域。
	//
	// 取点要小心：不能用「质心」。月牙形区域的质心会落在凹口外面（头发左侧那一绺就是这样，
	// 质心 (107,123) 其实在脸上），要用工具给的「内部点」。
	//
	// 袜子那块（脚踝以下到脚底那一条，工具里 id=15，bbox x[110,185] y[582,625]）是**先于肤色**
	// 泛洪的：它本来就夹在「左腿 / 右腿」两块肤色之间，晚泛就会被腿一并吃掉。
	// 所以肤色种子里不再有脚（原 (148,604) 那个点已交给袜子）。
	// ---------------------------------------------------------------------------
	private static readonly Vector2I[] HairSeeds =
	{
		new(230, 153),  // 右侧那一大团头发
		new(128, 28),   // 头顶左侧那一绺（月牙形）
	};
	private static readonly Vector2I[] SkinSeeds =
	{
		new(148, 137),  // 脸
		new(155, 198),  // 下巴（脸的下沿与脖子之间那一小块）
		new(163, 250),  // 脖子
		new(64, 336),   // 左手臂
		new(208, 287),  // 右肩
		new(227, 355),  // 右手臂
		new(125, 521),  // 左腿
		new(170, 520),  // 右腿
	};
	private static readonly Vector2I[] LipSeeds = { new(161, 162) };  // 嘴里的那块填色
	private static readonly Vector2I[] SockSeeds = { new(153, 583) }; // 脚踝到脚底那条「袜子」

	private sealed class BgItem
	{
		public string Label = "";
		public Color Top = Colors.White;
		public Color Bottom = Colors.White;
		public string Pattern = "none";  // none / grid / dots / clouds / stripes
		public Color Ink = Colors.White; // 花纹的颜色
	}

	// 数值怎么来的：先用 .workbuddy/tools/runs.ps1 在 girl_doll.png 上量出娃娃的落点
	// （单位「娃娃贴图像素」，原点在图片中心）：
	//   肩线 y≈-90、腰 y≈+20、裙摆最宽 y≈+135、裙摆左右 [-74,+54]、腿从 y≈+140 开始。
	// 再按「上沿盖住肩、下沿略宽于裙摆」定目标梯形，然后把每条裙子自己的
	// 「领口顶~裙摆最宽那一行」映射到这条梯形上（所以 Src 的底边要切在裙摆最宽处，
	// 免得手绘裙摆那圈弧线被压成「中间鼓、下摆收」）。
	private readonly List<WearItem> _dresses = new()
	{
		new WearItem { Label = "不穿", Tex = null },
		new WearItem
		{
			// 领口（粉）只占整图 13%，所以上沿要放得很宽，领口才不至于细成一根带子；
			// 下沿收到 310，大喇叭裙摆就不会变成一顶帐篷。Src 底边略过裙摆最宽那一行，
			// 为的是留住手绘裙摆那圈圆弧（切在最宽处的话下摆会是一条直线）。
			Label = "蓝心裙", Tex = AssetDir + "dress_blue_heart.png",
			Src = new Rect2(3, 3, 634, 330),
			TopY = -88, BotY = 142,
			TopCenterX = -68, TopWidth = 750,
			BotCenterX = -10, BotWidth = 310,
		},
		new WearItem
		{
			Label = "绿心裙", Tex = AssetDir + "dress_green_heart.png",
			Src = new Rect2(0, 6, 579, 530),
			TopY = -88, BotY = 150,
			TopCenterX = -42, TopWidth = 650,
			BotCenterX = -10, BotWidth = 300,
		},
		new WearItem
		{
			// 这条裙子是「左短右长」的斜裙：黄三角到腰、紫布往右下拖到裙摆以下，
			// 所以下沿比别的裙子低，让紫布能垂到裙摆外面。
			Label = "黄紫裙", Tex = AssetDir + "dress_yellow_purple.png",
			Src = new Rect2(8, 8, 529, 624),
			TopY = -85, BotY = 175,
			TopCenterX = 21, TopWidth = 300,
			BotCenterX = 10, BotWidth = 290,
		},
		// 下面三条是后来加的一批手绘素材。它们画在纸上时是「裙摆在左、腰带在右」横躺着的，
		// 裁切脚本（photo-to-sticker-png）输出后统一转了 90° 立起来，所以现在和上面几条同构：
		// 上窄腰带 + 下宽扇形。几何标定走 .workbuddy/tools/s2_fit.py（离线按 FlareShader
		// 同一套映射把部件叠到娃娃线稿上），不用反复启动 Godot。
	// 另外这批照片的现场光线偏暗，蜡笔色比第一批素材整体暗一档（彩色像素明度中位
	// 0.53~0.62，第一批是 0.74~0.82），已用 enhance_colors.py 自适应提亮到 0.76
	// 并加了饱和，否则贴到浅色背景上会显得「发黑」。PNG 里的色值就是校正后的，
	// 代码这边不用再管。
		new WearItem
		{
			// 腰带（绿）占源图宽度约 20%，要让肩上的腰带落到 ~100px 宽，整行就得铺到 493。
			Label = "橙蓝裙", Tex = AssetDir + "dress_orange_blue.png",
			Src = new Rect2(2, 2, 636, 578),
			TopY = -88, BotY = 142,
			TopCenterX = -11, TopWidth = 493,
			BotCenterX = -10, BotWidth = 310,
		},
		new WearItem
		{
			Label = "彩虹裙", Tex = AssetDir + "dress_rainbow.png",
			Src = new Rect2(2, 2, 636, 538),
			TopY = -88, BotY = 142,
			TopCenterX = 25, TopWidth = 420,
			BotCenterX = -10, BotWidth = 310,
		},
		new WearItem
		{
			Label = "粉橙裙", Tex = AssetDir + "dress_pink_orange.png",
			Src = new Rect2(2, 2, 636, 373),
			TopY = -88, BotY = 142,
			TopCenterX = 2, TopWidth = 440,
			BotCenterX = -10, BotWidth = 310,
		},
	};

	private readonly List<WearItem> _headbands = new()
	{
		new WearItem { Label = "不戴", Tex = null },
		new WearItem
		{
			// 发箍本来就是「拱 + 两条飘带」，上下等宽（不做梯形拉伸）就是等比缩放；
			// 这里只是借用同一套管线，让拱顶压在头顶稍上、内拱开口落在眉眼之上。
			Label = "蓝色发箍", Tex = AssetDir + "headband_blue.png",
			Src = new Rect2(10, 10, 411, 597),
			TopY = -325, BotY = -80,
			TopCenterX = -12, TopWidth = 180,
			BotCenterX = -12, BotWidth = 180,
		},
		// 后来加的两个手绘发饰。它们和那批裙子一样是「横躺着」画在纸上的，
		// 直接摆上去是个横压头顶的怪东西；后处理脚本
		// （photo-to-sticker-png/scripts/enhance_colors.py）把它们逆时针转了 90° 立起来，
		// 转完是「拱顶在上 + 两条垂腿」的形态，扣在头顶正好。
		// 高度按原图比例从宽度推出（原图内容框 429×589 / 484×630），别硬拉。
		new WearItem
		{
			Label = "蓝粉发箍", Tex = AssetDir + "headband_blue_pink.png",
			Src = new Rect2(8, 9, 429, 589),
			TopY = -340, BotY = -107,
			TopCenterX = -12, TopWidth = 170,
			BotCenterX = -12, BotWidth = 170,
		},
		new WearItem
		{
			Label = "黄色发饰", Tex = AssetDir + "headband_yellow.png",
			Src = new Rect2(5, 5, 484, 630),
			TopY = -346, BotY = -118,
			TopCenterX = -12, TopWidth = 175,
			BotCenterX = -12, BotWidth = 175,
		},
	};

	private readonly List<BgItem> _bgs = new()
	{
		new BgItem { Label = "画纸", Top = new Color("#fdfcf6"), Bottom = new Color("#e9f1fb"), Pattern = "grid", Ink = new Color("#c3d2ea") },
		new BgItem { Label = "樱粉", Top = new Color("#fff1f7"), Bottom = new Color("#ffd7e7"), Pattern = "dots", Ink = new Color("#ffffff") },
		new BgItem { Label = "晴空", Top = new Color("#8ed2ff"), Bottom = new Color("#ecf8ff"), Pattern = "clouds", Ink = new Color("#ffffff") },
		new BgItem { Label = "薄荷", Top = new Color("#dbf8ec"), Bottom = new Color("#a5e3c9"), Pattern = "stripes", Ink = new Color("#ffffff") },
	};

	// 「原色 / 无」都是白色 tint：着色器在 tint=白 时退化成原样，所以选它就等于没上色。
	private readonly List<ColorItem> _hairColors = new()
	{
		new ColorItem { Label = "原色", Tone = Colors.White },
		new ColorItem { Label = "乌黑", Tone = new Color("#33333d") },
		new ColorItem { Label = "栗棕", Tone = new Color("#8a5a3b") },
		new ColorItem { Label = "金黄", Tone = new Color("#efc04a") },
		new ColorItem { Label = "樱粉", Tone = new Color("#f4a6c8") },
		new ColorItem { Label = "海蓝", Tone = new Color("#6fb7e8") },
		new ColorItem { Label = "薰衣草", Tone = new Color("#b79ce8") },
	};

	private readonly List<ColorItem> _lipColors = new()
	{
		new ColorItem { Label = "无", Tone = Colors.White },
		new ColorItem { Label = "蜜桃", Tone = new Color("#ff9aa2") },
		new ColorItem { Label = "正红", Tone = new Color("#e8455a") },
		new ColorItem { Label = "珊瑚", Tone = new Color("#ff8a5c") },
		new ColorItem { Label = "莓紫", Tone = new Color("#c2569b") },
	};

	private readonly List<ColorItem> _skinTones = new()
	{
		new ColorItem { Label = "冷白", Tone = Colors.White },          // 纸原本的白
		new ColorItem { Label = "米色", Tone = new Color("#f6e2cb") },
		new ColorItem { Label = "暖黄", Tone = new Color("#f3ce8e") },
	};

	// 袜子就是脚踝以下那一条，颜色选的都是常见袜子色（和头发/唇彩一套做法，只是换了个区域）
	private readonly List<ColorItem> _sockColors = new()
	{
		new ColorItem { Label = "原色", Tone = Colors.White },
		new ColorItem { Label = "粉红", Tone = new Color("#ff9ec4") },
		new ColorItem { Label = "天蓝", Tone = new Color("#7fc4ef") },
		new ColorItem { Label = "鹅黄", Tone = new Color("#ffe07a") },
		new ColorItem { Label = "薄荷", Tone = new Color("#93e6c3") },
		new ColorItem { Label = "薰衣草", Tone = new Color("#b79ce8") },
		new ColorItem { Label = "灰蓝", Tone = new Color("#8fa3c8") },
	};

	// ---------- 运行时状态 ----------
	private readonly List<ImageTexture> _bgTextures = new();
	private readonly Dictionary<Button, int> _categoryIndex = new();

	// 物品栏当前挂着的按钮，与 _stripIndices 一一对应（刷新高亮只改 Modulate，不重建节点）
	private readonly List<BaseButton> _stripButtons = new();
	private readonly List<int> _stripIndices = new();

	private int _activeCategory; // 0裙子 1发箍 2头发 3唇彩 4肤色 5袜子 6装饰 7背景
	private int _dressIndex = InitialDress;
	private int _headbandIndex = InitialHeadband;
	private int _hairIndex = InitialHair;
	private int _lipIndex = InitialLip;
	private int _skinIndex = InitialSkin;
	private int _sockIndex = InitialSock;
	private int _bgIndex = InitialBg;

	// ---------- 装饰贴纸 ----------
	private readonly List<DecoItem> _decos = new();       // 可用贴纸（贴图/命中表在 _Ready 里生成）
	private readonly List<Sticker> _stickers = new();     // 已经贴上去的（顺序即绘制顺序）
	private Sticker? _selectedSticker;                    // 当前选中的那张（滑块/删掉按钮绑它）
	private Sticker? _dragSticker;
	private Vector2 _dragStickerGrab;
	private int _stickerSpot;                             // 新贴纸该落在 DecoSpots 的哪一格
	private Sprite2D _decoFrame = null!;                  // 选中贴纸的高亮外框
	private HBoxContainer _rotationRow = null!;
	private HSlider _rotationSlider = null!;
	private Label _rotationValue = null!;
	private Button _deleteButton = null!;
	private Vector2 _center;
	private Rect2 _stageRect;        // 舞台可点区域（底部面板以上）
	private Tween? _toastTween;
	private int _photoCounter;

	// 命中判定要读「源贴图那一像素的 alpha」，按贴图路径缓存下来，别每次按下都重新加载
	private readonly Dictionary<string, Image> _sourceImages = new();

	// 区域掩码的实测像素数（自测用来断言「泛洪没漏到隔壁区域去」）
	private int _maskHair, _maskSkin, _maskLip, _maskSock;

	// 拖动状态：按下的那件穿戴物，以及按下点相对它当前位移的差
	private WearItem? _dragItem;
	private Vector2 _dragGrab;       // 娃娃贴图像素
	private bool _dragging;

	public override void _Ready()
	{
		_background = GetNode<TextureRect>("Stage/Background");
		_character = GetNode<Node2D>("Stage/Character");
		_doll = GetNode<Sprite2D>("Stage/Character/Doll");
		_dress = GetNode<Sprite2D>("Stage/Character/Dress");
		_headband = GetNode<Sprite2D>("Stage/Character/Headband");
		_topBar = GetNode<HBoxContainer>("UI/TopBar");
		_categoryTabs = GetNode<HBoxContainer>("UI/BottomPanel/VBox/CategoryTabs");
		_itemScroll = GetNode<ScrollContainer>("UI/BottomPanel/VBox/ItemScroll");
		_itemStrip = GetNode<HBoxContainer>("UI/BottomPanel/VBox/ItemScroll/ItemStrip");
		_homeButton = GetNode<Button>("UI/TopBar/HomeButton");
		_resetButton = GetNode<Button>("UI/TopBar/ResetButton");
		_photoButton = GetNode<Button>("UI/TopBar/PhotoButton");
		_scaleRow = GetNode<HBoxContainer>("UI/BottomPanel/VBox/ScaleRow");
		_scaleSlider = GetNode<HSlider>("UI/BottomPanel/VBox/ScaleRow/ScaleSlider");
		_scaleValue = GetNode<Label>("UI/BottomPanel/VBox/ScaleRow/ScaleValue");
		_sfx = GetNode<AudioStreamPlayer>("SfxPlayer");
		_toast = GetNode<Label>("Toast");

		// 音效文件缺失时静默跳过（项目里 sfx/ding.wav 目前并不存在）
		if (ResourceLoader.Exists(DingPath))
			_sfx.Stream = GD.Load<AudioStream>(DingPath);

		// 裙子和发箍共用同一个「梯形拉伸」着色器，但两者的参数不同，所以各配一个材质
		var flare = new Shader { Code = FlareShader };
		_dress.Material = new ShaderMaterial { Shader = flare };
		_headband.Material = new ShaderMaterial { Shader = flare };

		// 娃娃自己用一个「按区域涂色」的着色器（头发 / 唇彩 / 肤色）
		_doll.Material = new ShaderMaterial { Shader = new Shader { Code = DollTintShader } };
		var regionMap = BuildRegionMap();
		((ShaderMaterial)_doll.Material).SetShaderParameter("region_map", regionMap);
		UpdateDollTint();

		// 背景纹理预生成
		for (int i = 0; i < _bgs.Count; i++)
			_bgTextures.Add(MakeBackgroundTexture(_bgs[i]));

		// 装饰贴纸的贴图也是代码画的，开局一次性生成
		BuildDecoTextures();
		// 选中贴纸的高亮外框：挂在角色节点下，跟着那张贴纸的位移/旋转/缩放走
		_decoFrame = new Sprite2D
		{
			Texture = MakeDecoFrameTexture(),
			ZIndex = ZDecoFrame,
			Visible = false,
		};
		_character.AddChild(_decoFrame);

		BuildTheme();
		BuildUi();
		BuildCategoryTabs();

		SetProcessInput(true); // 拖动穿戴物靠 _Input 实现，确保输入回调是打开的
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		LayoutStage();
		ApplyInitialLook();
		SelectCategory(0, silent: true);

		GetViewport().SizeChanged += LayoutStage;

		GD.Print($"[HandDrawn] ready. selftest = {SelftestFlag.Describe()}");

		// 自测开关由首页路由过来：只有 selftest.flag 的内容正好是 "hand" 时才跑自测。
		if (SelftestFlag.Read() == SelftestFlag.TokenHand)
			_ = RunSelfTestAsync();
		else
			_ = ShowHintAsync();
	}

	// ================= 布局 =================

	private void LayoutStage()
	{
		Vector2 size = Size.X > 0 && Size.Y > 0 ? Size : new Vector2(720, 1280);

		// 关键：让 Stage 与 UI 容器铺满全屏，否则里面的顶栏/底部面板宽度会塌缩为 0
		GetNode<Control>("Stage").SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		GetNode<Control>("UI").SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		float stageH = Mathf.Max(size.Y - PanelHeight, 400);
		_center = new Vector2(size.X * 0.5f, stageH * 0.54f);
		_character.Position = _center;

		// 舞台可点区域：面板以上。用来判断一次按下是「搬穿戴物」还是「按面板」
		_stageRect = new Rect2(0, 0, size.X, Mathf.Max(size.Y - PanelHeight, 200));

		_doll.Texture = GD.Load<Texture2D>(AssetDir + "girl_doll.png");
		_doll.Scale = Vector2.One * DollScale;

		// 刷新穿着部件的位置（窗口缩放后要重算）
		ApplyDress(_dressIndex, silent: true);
		ApplyHeadband(_headbandIndex, silent: true);
		// 贴纸也挂在 _character 下、用的是同一套「娃娃贴图像素」，所以摆法要重算一遍
		foreach (var st in _stickers)
			ApplySticker(st);
	}

	// ================= 主题与 UI 构建 =================

	private void BuildTheme()
	{
		var ui = GetNode<Control>("UI");
		ui.Theme = GameArt.MakeUiTheme();
		ui.MouseFilter = MouseFilterEnum.Ignore;
		GetNode<Control>("Stage").MouseFilter = MouseFilterEnum.Ignore;
		_background.MouseFilter = MouseFilterEnum.Ignore;
		_background.StretchMode = TextureRect.StretchModeEnum.Scale;
	}

	private void BuildUi()
	{
		// ---- 顶栏：返回 / 重置 / 拍照（顺序与场景文件一致：返回 → 重置 → spacer → 拍照）----
		_topBar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		_topBar.OffsetLeft = 20;
		_topBar.OffsetTop = 20;
		_topBar.OffsetRight = -20;
		_topBar.OffsetBottom = 112;
		_topBar.AddThemeConstantOverride("separation", 16);

		GameArt.StyleButton(_homeButton, new Color("#8f7bff"), Colors.White);
		_homeButton.CustomMinimumSize = new Vector2(150, 92);
		_homeButton.Text = "返回";
		_homeButton.Pressed += OnHomePressed;

		GameArt.StyleButton(_resetButton, new Color("#ff8f6b"), Colors.White);
		_resetButton.CustomMinimumSize = new Vector2(170, 92);
		_resetButton.Text = "重置";
		_resetButton.Pressed += OnResetPressed;

		var spacer = new Control();
		spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_topBar.AddChild(spacer);
		// spacer 必须插到「重置」之后（索引 2），插错会把按钮挤到右边
		_topBar.MoveChild(spacer, 2);

		GameArt.StyleButton(_photoButton, new Color("#4fa8ff"), Colors.White);
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
		var panelBox = GameArt.MakeBox(new Color(1, 1, 1, 0.96f), 26);
		panelBox.ContentMarginLeft = 16;
		panelBox.ContentMarginRight = 16;
		panelBox.ContentMarginTop = 10;
		panelBox.ContentMarginBottom = 18;
		panel.AddThemeStyleboxOverride("panel", panelBox);

		var vbox = GetNode<VBoxContainer>("UI/BottomPanel/VBox");
		vbox.AddThemeConstantOverride("separation", 12);

		// ---- 物品栏（横向滚动，支持触摸拖动）----
		_itemScroll.CustomMinimumSize = new Vector2(0, 190);
		_itemStrip.AddThemeConstantOverride("separation", 16);

		// 手机上那根细滚动条基本拖不动，改成物品栏左右两个大箭头翻页
		_stripPager = new StripPager(_itemScroll, PlayPop);

		// ---- 「大小」滑块：只对裙子 / 发箍有意义，切到别的分类就整行藏起来 ----
		_scaleRow.AddThemeConstantOverride("separation", 16);
		var title = GetNode<Label>("UI/BottomPanel/VBox/ScaleRow/ScaleTitle");
		title.Text = "大小";
		title.VerticalAlignment = VerticalAlignment.Center;
		title.AddThemeFontSizeOverride("font_size", 28);
		title.AddThemeColorOverride("font_color", new Color("#44445a"));

		_scaleSlider.MinValue = MinUserScale;
		_scaleSlider.MaxValue = MaxUserScale;
		_scaleSlider.Step = 0.05;
		_scaleSlider.Value = 1.0;
		_scaleSlider.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_scaleSlider.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
		_scaleSlider.CustomMinimumSize = new Vector2(0, 48);
		_scaleSlider.ValueChanged += OnScaleChanged;

		_scaleValue.CustomMinimumSize = new Vector2(110, 0);
		_scaleValue.VerticalAlignment = VerticalAlignment.Center;
		_scaleValue.HorizontalAlignment = HorizontalAlignment.Right;
		_scaleValue.AddThemeFontSizeOverride("font_size", 28);
		_scaleValue.AddThemeColorOverride("font_color", new Color("#44445a"));
		_scaleValue.Text = "1.00×";

		// ---- 「旋转」行 + 「删掉」：只在选中了一张贴纸时出现（场景文件里没有这几个节点，代码建）----
		_rotationRow = new HBoxContainer();
		_rotationRow.AddThemeConstantOverride("separation", 16);
		var rotTitle = new Label
		{
			Text = "旋转",
			VerticalAlignment = VerticalAlignment.Center,
		};
		rotTitle.AddThemeFontSizeOverride("font_size", 28);
		rotTitle.AddThemeColorOverride("font_color", new Color("#44445a"));
		_rotationRow.AddChild(rotTitle);

		_rotationSlider = new HSlider
		{
			MinValue = DecoMinRot,
			MaxValue = DecoMaxRot,
			Step = 1,
			Value = 0,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
			CustomMinimumSize = new Vector2(0, 48),
		};
		_rotationSlider.ValueChanged += OnRotationChanged;
		_rotationRow.AddChild(_rotationSlider);

		_rotationValue = new Label
		{
			CustomMinimumSize = new Vector2(96, 0),
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Right,
			Text = "0°",
		};
		_rotationValue.AddThemeFontSizeOverride("font_size", 28);
		_rotationValue.AddThemeColorOverride("font_color", new Color("#44445a"));
		_rotationRow.AddChild(_rotationValue);

		_deleteButton = new Button
		{
			Text = "删掉",
			CustomMinimumSize = new Vector2(120, 64),
		};
		GameArt.StyleButton(_deleteButton, new Color("#ff8f6b"), Colors.White, fontSize: 28);
		_deleteButton.Pressed += () =>
		{
			PlayPop(_deleteButton);
			DeleteSticker();
		};
		_rotationRow.AddChild(_deleteButton);

		vbox.AddChild(_rotationRow);
		// 插到「大小」下面：0 分类标签 / 1 大小 / 2 旋转 / 3 物品栏
		vbox.MoveChild(_rotationRow, 2);

		// ---- Toast 提示 ----
		// 水平必须 Both（锚在中心时，默认的 End 会让提示往右溢出被裁掉）；
		// 垂直用 End（顶边钉在 ToastTop，提示再长也不顶到上面的顶栏）。
		_toast.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
		_toast.GrowHorizontal = GrowDirection.Both;
		_toast.GrowVertical = GrowDirection.End;
		_toast.OffsetTop = ToastTop;
		_toast.OffsetBottom = ToastTop;
		_toast.HorizontalAlignment = HorizontalAlignment.Center;
		_toast.MouseFilter = MouseFilterEnum.Ignore;
		// 裙子/发箍的 z_index 是 10/20，Toast 不给更高的层号就会被衣服盖住
		_toast.ZIndex = 100;
		_toast.AddThemeFontSizeOverride("font_size", 34);
		_toast.AddThemeColorOverride("font_color", Colors.White);
		_toast.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.65f));
		_toast.AddThemeConstantOverride("outline_size", 8);
		var toastBox = GameArt.MakeBox(new Color(0.12f, 0.12f, 0.16f, 0.82f), 20);
		toastBox.ContentMarginLeft = toastBox.ContentMarginRight = 22;
		toastBox.ContentMarginTop = toastBox.ContentMarginBottom = 10;
		_toast.AddThemeStyleboxOverride("normal", toastBox);
		_toast.Visible = false;
	}

	private void BuildCategoryTabs()
	{
		_categoryTabs.AddThemeConstantOverride("separation", 10);
		// 八个标签挤在一行里，字号和内边距都收小一点才不折行（见 StyleTabButton）
		string[] cats = { "裙子", "发箍", "头发", "唇彩", "肤色", "袜子", "装饰", "背景" };
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
	}

	private void RefreshCategoryTabs()
	{
		foreach (var kv in _categoryIndex)
		{
			bool active = kv.Value == _activeCategory;
			Color bg = active ? new Color("#ffb347") : new Color("#f0f0f5");
			Color font = active ? Colors.White : new Color("#44445a");
			StyleTabButton(kv.Key, bg, font, active ? new Color("#e08a00") : null);
		}
	}

	/// <summary>
	/// 分类标签专用的紧凑按钮：通用 <see cref="GameArt.StyleButton"/> 的左右内边距是 14，
	/// 「裙子」两字在 font 26 下宽约 52，加起来 80 > 每个标签分到的 77px，八个标签就放不下了。
	/// 这里把左右内边距压到 6（每个标签 64px），一行才装得下。
	/// </summary>
	private static void StyleTabButton(Button b, Color bg, Color font, Color? border)
	{
		b.AddThemeStyleboxOverride("normal", TabBox(bg, border));
		b.AddThemeStyleboxOverride("hover", TabBox(bg.Lightened(0.12f), border));
		b.AddThemeStyleboxOverride("pressed", TabBox(bg.Darkened(0.18f), border));
		b.AddThemeColorOverride("font_color", font);
		b.AddThemeColorOverride("font_hover_color", font);
		b.AddThemeColorOverride("font_pressed_color", font);
		b.AddThemeColorOverride("font_focus_color", font);
		b.AddThemeFontSizeOverride("font_size", 26);
	}

	private static StyleBoxFlat TabBox(Color bg, Color? border)
	{
		var sb = GameArt.MakeBox(bg, 20, border);
		sb.ContentMarginLeft = 6;
		sb.ContentMarginRight = 6;
		return sb;
	}

	// ================= 物品栏 =================

	private void SelectCategory(int index, bool silent = false)
	{
		_activeCategory = index;
		// 切走「装饰」就别再举着那张贴纸了（旋转行跟着它一起收起来）
		if (index != CatDeco)
			SelectSticker(null);
		RefreshCategoryTabs();
		RebuildItemStrip();
		RefreshAdjustRows();
		if (!silent) PlayDing();
	}

	/// <summary>
	/// 「大小 / 旋转 / 删掉」这几行有没有得调：
	/// 选中了一张贴纸 → 三样都给；否则当前这件穿戴物有贴图 → 只给「大小」；都没有就整行藏起来。
	/// </summary>
	private void RefreshAdjustRows()
	{
		if (_selectedSticker != null)
		{
			var st = _selectedSticker;
			_scaleRow.Visible = true;
			_scaleSlider.SetValueNoSignal(Mathf.Clamp(st.UserScale, MinUserScale, MaxUserScale));
			_scaleValue.Text = $"{st.UserScale:0.00}×";
			_rotationRow.Visible = true;
			_rotationSlider.SetValueNoSignal(Mathf.Clamp(st.Deg, DecoMinRot, DecoMaxRot));
			_rotationValue.Text = $"{st.Deg:0}°";
			return;
		}

		_rotationRow.Visible = false;
		var current = CurrentWearItem();
		_scaleRow.Visible = current?.Tex != null;
		if (current == null)
			return;
		// 换了一件之后滑块要跟上它的当前缩放（改值会触发 ValueChanged，这里先屏蔽）
		_scaleSlider.SetValueNoSignal(Mathf.Clamp(current.UserScale, MinUserScale, MaxUserScale));
		_scaleValue.Text = $"{current.UserScale:0.00}×";
	}

	/// <summary>当前分类正在编辑的那件穿戴物（只有裙子 / 发箍两档有）。</summary>
	private WearItem? CurrentWearItem() => _activeCategory switch
	{
		0 => _dresses[_dressIndex],
		1 => _headbands[_headbandIndex],
		_ => null,
	};

	private void RebuildItemStrip()
	{
		foreach (var child in _itemStrip.GetChildren())
		{
			// 先 RemoveChild 再 QueueFree：QueueFree 要到帧末才真正删除，
			// 只调它的话这一帧容器里会同时挂着新旧两套按钮（布局会闪一下）。
			_itemStrip.RemoveChild(child);
			child.QueueFree();
		}
		_stripButtons.Clear();
		_stripIndices.Clear();

		switch (_activeCategory)
		{
			case 0:
				BuildWearStrip(_dresses, i => ApplyDress(i));
				break;
			case 1:
				BuildWearStrip(_headbands, i => ApplyHeadband(i));
				break;
			case 2:
				BuildColorStrip(_hairColors, i => ApplyHairColor(i));
				break;
			case 3:
				BuildColorStrip(_lipColors, i => ApplyLipColor(i));
				break;
			case 4:
				BuildColorStrip(_skinTones, i => ApplySkinTone(i));
				break;
			case 5:
				BuildColorStrip(_sockColors, i => ApplySockColor(i));
				break;
			case CatDeco:
				BuildDecoStrip();
				break;
			case 7:
				BuildBgStrip();
				break;
		}

		RefreshStripHighlight();
	}

	private void AddStripButton(BaseButton b, int itemIndex)
	{
		_itemStrip.AddChild(b);
		_stripButtons.Add(b);
		_stripIndices.Add(itemIndex);
	}

	private void BuildWearStrip(List<WearItem> items, System.Action<int> apply)
	{
		for (int i = 0; i < items.Count; i++)
		{
			var item = items[i];
			BaseButton b;
			if (item.Tex is null)
			{
				var btn = new Button
				{
					Text = item.Label,
					CustomMinimumSize = new Vector2(150, 150),
				};
				GameArt.StyleButton(btn, new Color("#f0f0f5"), new Color("#44445a"), radius: 22);
				btn.AddThemeFontSizeOverride("font_size", 38);
				b = btn;
			}
			else
			{
				b = MakeItemButton(item.Label, GD.Load<Texture2D>(item.Tex));
			}

			// 闭包捕获当次循环的局部变量，避免被后续循环改掉
			int idx = i;
			b.Pressed += () =>
			{
				PlayPop(b);
				apply(idx);
			};
			AddStripButton(b, idx);
		}
	}

	/// <summary>颜色分类（头发 / 唇彩 / 肤色 / 袜子）：一枚色块 + 颜色名。</summary>
	private void BuildColorStrip(List<ColorItem> colors, System.Action<int> apply)
	{
		for (int i = 0; i < colors.Count; i++)
		{
			var item = colors[i];
			var b = new Button
			{
				Text = item.Label,
				CustomMinimumSize = new Vector2(150, 150),
				Icon = MakeSwatchTexture(item.Tone),
				ExpandIcon = true,
				IconAlignment = HorizontalAlignment.Center,
				VerticalIconAlignment = VerticalAlignment.Top,
			};
			b.AddThemeColorOverride("font_color", new Color("#44445a"));
			b.AddThemeColorOverride("font_hover_color", new Color("#44445a"));
			b.AddThemeColorOverride("font_pressed_color", new Color("#44445a"));
			b.AddThemeColorOverride("font_focus_color", new Color("#44445a"));
			b.AddThemeFontSizeOverride("font_size", 28);
			b.AddThemeStyleboxOverride("normal", GameArt.MakeBox(new Color(1, 1, 1, 0.9f), 22));
			b.AddThemeStyleboxOverride("hover", GameArt.MakeBox(new Color(1, 1, 1, 1f), 22));
			b.AddThemeStyleboxOverride("pressed", GameArt.MakeBox(new Color(0.92f, 0.92f, 1.0f), 22));
			int idx = i;
			b.Pressed += () =>
			{
				PlayPop(b);
				apply(idx);
			};
			AddStripButton(b, idx);
		}
	}

	/// <summary>一枚圆角色块，当颜色按钮的图标用。</summary>
	private static ImageTexture MakeSwatchTexture(Color tone, int size = 96, int radius = 22)
	{
		var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		img.Fill(Colors.Transparent);
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				// 四个圆角处挖掉：像素到两个边界的最小距离小于半径就落在圆外
				float dx = Mathf.Max(0, Mathf.Max(radius - x, x - (size - 1 - radius)));
				float dy = Mathf.Max(0, Mathf.Max(radius - y, y - (size - 1 - radius)));
				if (dx * dx + dy * dy > (float)radius * radius)
					continue;
				img.SetPixel(x, y, tone);
			}
		}
		return ImageTexture.CreateFromImage(img);
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
				Icon = MakeBackgroundTexture(bg, 150, 96),
				ExpandIcon = true,
				IconAlignment = HorizontalAlignment.Center,
				VerticalIconAlignment = VerticalAlignment.Top,
			};
			b.AddThemeColorOverride("font_color", new Color("#44445a"));
			b.AddThemeColorOverride("font_hover_color", new Color("#44445a"));
			b.AddThemeColorOverride("font_pressed_color", new Color("#44445a"));
			b.AddThemeColorOverride("font_focus_color", new Color("#44445a"));
			b.AddThemeFontSizeOverride("font_size", 28);
			b.AddThemeStyleboxOverride("normal", GameArt.MakeBox(new Color(1, 1, 1, 0.9f), 22));
			b.AddThemeStyleboxOverride("hover", GameArt.MakeBox(new Color(1, 1, 1, 1f), 22));
			b.AddThemeStyleboxOverride("pressed", GameArt.MakeBox(new Color(0.92f, 0.92f, 1.0f), 22));
			int idx = i;
			b.Pressed += () =>
			{
				PlayPop(b);
				ApplyBackground(idx);
			};
			AddStripButton(b, idx);
		}
	}

	/// <summary>
	/// 装饰贴纸的物品栏：点一下不是「换掉身上那件」，而是**再贴一张新的**上去
	/// （和裙子/发箍的单选不一样，这里可以贴很多张）。
	/// </summary>
	private void BuildDecoStrip()
	{
		for (int i = 0; i < _decos.Count; i++)
		{
			var item = _decos[i];
			var tb = MakeItemButton(item.Label, item.Tex);
			int idx = i;
			tb.Pressed += () =>
			{
				PlayPop(tb);
				AddSticker(idx);
			};
			AddStripButton(tb, idx);
		}
	}

	/// <summary>大号物品预览按钮（贴纸缩略图 + 名称角标）</summary>
	private TextureButton MakeItemButton(string label, Texture2D tex)
	{
		var tb = new TextureButton
		{
			CustomMinimumSize = new Vector2(150, 150),
			IgnoreTextureSize = true,
			StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
			TextureNormal = tex,
		};
		tb.AddThemeStyleboxOverride("normal", GameArt.MakeBox(new Color(1, 1, 1, 0.85f), 22,
			new Color(0.75f, 0.75f, 0.85f)));
		tb.AddThemeStyleboxOverride("hover", GameArt.MakeBox(new Color(1, 1, 1, 0.95f), 22));
		tb.AddThemeStyleboxOverride("pressed", GameArt.MakeBox(new Color(0.92f, 0.92f, 1.0f), 22));
		tb.AddThemeStyleboxOverride("focus", GameArt.MakeBox(new Color(0, 0, 0, 0f), 22));

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

	/// <summary>
	/// 就地刷新物品栏高亮：只改按钮的 Modulate，不重建节点，
	/// 这样点击的弹跳动画才播得完。
	/// </summary>
	private void RefreshStripHighlight()
	{
		for (int k = 0; k < _stripButtons.Count; k++)
		{
			int idx = _stripIndices[k];
			bool active = _activeCategory switch
			{
				0 => idx == _dressIndex,
				1 => idx == _headbandIndex,
				2 => idx == _hairIndex,
				3 => idx == _lipIndex,
				4 => idx == _skinIndex,
				5 => idx == _sockIndex,
				6 => _selectedSticker != null && idx == _selectedSticker.Type,
				7 => idx == _bgIndex,
				_ => false,
			};
			_stripButtons[k].Modulate = active ? HighlightTint : Colors.White;
			// 切分类 / 换选中项之后，把它滚进可视范围（已可见时不动）
			if (active)
				_stripPager.EnsureVisible(_stripButtons[k]);
		}
	}

	// ================= 换装逻辑 =================

	private void ApplyDress(int index, bool silent = false)
	{
		_dressIndex = index;
		ApplyWear(_dress, _dresses[index]);
		RefreshAdjustRows();   // 每件裙子各自记着自己的「大小」，换了件滑块要跟上
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ApplyHeadband(int index, bool silent = false)
	{
		_headbandIndex = index;
		ApplyWear(_headband, _headbands[index]);
		RefreshAdjustRows();
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	/// <summary>
	/// 把一件手绘部件摆到娃娃身上，并把它「抻」成贴合娃娃的形状。
	///
	/// 三步：
	/// <list type="number">
	/// <item>由 <see cref="WearItem"/> 的上下沿算出目标包围盒（娃娃贴图像素）；</item>
	/// <item>把 Sprite2D 的矩形摆成「源裁切范围正好铺满包围盒」——因为 Sprite2D 画的是整张贴图，
	/// 要让裁切范围对上包围盒，整张图就得相应地放大、并把中心偏移过去；</item>
	/// <item>把这四个梯形参数交给着色器，逐像素做拉伸。</item>
	/// </list>
	/// 记得 Sprite2D 的 Position/Scale 是画布像素，而这里的几何量是「娃娃贴图像素」，所以要乘 <see cref="DollScale"/>。
	/// </summary>
	private void ApplyWear(Sprite2D sp, WearItem it)
	{
		if (it.Tex is null)
		{
			sp.Visible = false;
			return;
		}

		var tex = GD.Load<Texture2D>(it.Tex);
		sp.Texture = tex;
		sp.Visible = true;

		float texW = tex.GetWidth();
		float texH = tex.GetHeight();
		Vector2 uvMin = it.Src.Position / new Vector2(texW, texH);
		Vector2 uvMax = (it.Src.Position + it.Src.Size) / new Vector2(texW, texH);

		// 目标梯形（已含「大小」缩放和拖动位移）
		var g = WearGeometry(it);
		var bbox = new Rect2(g.Left, g.TopY, g.Width, g.Height);

		// 整张贴图要画成多大：裁切范围占贴图的比例越小，整张图就得放得越大
		var spriteSize = new Vector2(bbox.Size.X / (uvMax.X - uvMin.X), bbox.Size.Y / (uvMax.Y - uvMin.Y)) * DollScale;
		sp.Scale = spriteSize / new Vector2(texW, texH);
		// 再把「裁切范围的中心」对到包围盒中心（整张图的中心因此要偏出去）
		Vector2 uvCenter = (uvMin + uvMax) * 0.5f;
		sp.Position = bbox.GetCenter() * DollScale - (uvCenter - new Vector2(0.5f, 0.5f)) * spriteSize;

		var mat = (ShaderMaterial)sp.Material;
		mat.SetShaderParameter("uv_min", uvMin);
		mat.SetShaderParameter("uv_max", uvMax);
		mat.SetShaderParameter("cen_top", (g.CenTop - g.Left) / g.Width);
		mat.SetShaderParameter("cen_bot", (g.CenBot - g.Left) / g.Width);
		mat.SetShaderParameter("span_top", g.SpanTop / g.Width);
		mat.SetShaderParameter("span_bot", g.SpanBot / g.Width);
	}

	/// <summary>取这件穿戴物的源贴图（命中判定要按 alpha 抠形状），按路径缓存。</summary>
	private Image SolidMaskOf(WearItem it)
	{
		string path = it.Tex!;
		if (!_sourceImages.TryGetValue(path, out var img))
		{
			img = GD.Load<Texture2D>(path).GetImage();
			_sourceImages[path] = img;
		}
		return img;
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

	// ================= 头发 / 唇彩 / 肤色 / 袜子 =================

	private void ApplyHairColor(int index, bool silent = false)
	{
		_hairIndex = index;
		UpdateDollTint();
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ApplyLipColor(int index, bool silent = false)
	{
		_lipIndex = index;
		UpdateDollTint();
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ApplySkinTone(int index, bool silent = false)
	{
		_skinIndex = index;
		UpdateDollTint();
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	private void ApplySockColor(int index, bool silent = false)
	{
		_sockIndex = index;
		UpdateDollTint();
		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	/// <summary>把当前选中的四种颜色写进娃娃的着色器。选「原色 / 无」就是白色，等于不上色。</summary>
	private void UpdateDollTint()
	{
		if (_doll.Material is not ShaderMaterial mat)
			return;
		mat.SetShaderParameter("hair_tint", ToVec3(_hairColors[_hairIndex].Tone));
		mat.SetShaderParameter("lip_tint", ToVec3(_lipColors[_lipIndex].Tone));
		mat.SetShaderParameter("skin_tint", ToVec3(_skinColors_Active.Tone));
		mat.SetShaderParameter("sock_tint", ToVec3(_sockColors[_sockIndex].Tone));
	}

	/// <summary>当前肤色（单独抽出来，自测里要直接断言它）。</summary>
	private ColorItem _skinColors_Active => _skinTones[_skinIndex];

	private static Vector3 ToVec3(Color c) => new(c.R, c.G, c.B);

	// ================= 大小与旋转 =================

	private void OnScaleChanged(double value)
	{
		// 举着一张贴纸时，「大小」改的是那张贴纸
		if (_selectedSticker != null)
		{
			_selectedSticker.UserScale = (float)value;
			_scaleValue.Text = $"{_selectedSticker.UserScale:0.00}×";
			ApplySticker(_selectedSticker);
			return;
		}

		var current = CurrentWearItem();
		if (current?.Tex == null)
			return;
		current.UserScale = (float)value;
		_scaleValue.Text = $"{current.UserScale:0.00}×";
		ApplyDress(_dressIndex, silent: true);
		ApplyHeadband(_headbandIndex, silent: true);
	}

	private void OnRotationChanged(double value)
	{
		if (_selectedSticker == null)
			return;
		_selectedSticker.Deg = (float)value;
		_rotationValue.Text = $"{_selectedSticker.Deg:0}°";
		ApplySticker(_selectedSticker);
	}

	// ================= 拖动穿戴物 =================
	//
	// 和 StickerGame 一样的路子：按下 → 看有没有戳到穿戴物的「实心处」→ 拖动改位移。
	// 命中判定不能只看包围盒：梯形拉伸之后包围盒里大片是透明的
	// （蓝心裙的包围盒有 760 宽，裙子本体只有 300），照包围盒判会「隔着老远就抓走裙子」。
	// 所以这里按着色器同一套公式反算出源贴图坐标，再看那一像素的 alpha。

	public override void _Input(InputEvent @event)
	{
		bool consumed = false;
		switch (@event)
		{
			case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
				consumed = mb.Pressed ? BeginDrag(mb.Position) : EndDrag();
				break;
			case InputEventMouseMotion mm:
				consumed = MoveDrag(mm.Position);
				break;
			// 触摸屏（Android）：按下 / 拖动 / 抬起
			case InputEventScreenTouch st:
				consumed = st.Pressed ? BeginDrag(st.Position) : EndDrag();
				break;
			case InputEventScreenDrag sd:
				consumed = MoveDrag(sd.Position);
				break;
		}

		// 正在搬东西时把事件吃掉，否则手指划过底部面板会让它跟着一起滚
		if (consumed)
			GetViewport().SetInputAsHandled();
	}

	/// <summary>按下：看看有没有戳到已经贴上去的贴纸 / 穿在身上的部件。返回 true 表示这次按下归我们管。</summary>
	private bool BeginDrag(Vector2 canvasPos)
	{
		if (_dragging)
			return false; // 已经按住一件了（多半是鼠标/触摸的重复事件）

		// 点在底部面板或顶栏上就别抢，交给 UI
		if (!_stageRect.HasPoint(canvasPos))
			return false;

		// 「装饰」分类下：贴纸压在最上面，先判它们。
		// 只在装饰分类里判是有意的——在别的分类里贴纸会挡住裙子和发箍，那样就抓不到衣服了。
		if (_activeCategory == CatDeco)
		{
			var st = HitSticker(canvasPos);
			if (st != null)
			{
				SelectSticker(st);
				_dragSticker = st;
				_dragStickerGrab = ToDollPoint(canvasPos) - st.Pos;
				_dragging = true;
				return true;
			}
			// 点到空处：把手上那张放下（不消费事件，交给默认处理）
			SelectSticker(null);
			return false;
		}

		// 发箍画在裙子上面，先判它
		WearItem? hit = HitTest(_headbands[_headbandIndex], canvasPos)
			?? HitTest(_dresses[_dressIndex], canvasPos);
		if (hit == null)
			return false;

		_dragItem = hit;
		_dragGrab = ToDollPoint(canvasPos) - hit.Drag;
		_dragging = true;
		return true;
	}

	private bool MoveDrag(Vector2 canvasPos)
	{
		if (!_dragging)
			return false;
		if (_dragSticker != null)
		{
			_dragSticker.Pos = ToDollPoint(canvasPos) - _dragStickerGrab;
			ApplySticker(_dragSticker);
			return true;
		}
		if (_dragItem == null)
			return false;
		_dragItem.Drag = ToDollPoint(canvasPos) - _dragGrab;
		ApplyDress(_dressIndex, silent: true);
		ApplyHeadband(_headbandIndex, silent: true);
		return true;
	}

	private bool EndDrag()
	{
		if (!_dragging)
			return false;
		_dragging = false;
		_dragSticker = null;
		_dragItem = null;
		return true;
	}

	/// <summary>画布坐标 → 娃娃贴图像素坐标（原点在娃娃图片中心）。</summary>
	private Vector2 ToDollPoint(Vector2 canvasPos) => (canvasPos - _center) / DollScale;

	/// <summary>戳中了这件穿戴物的实心处吗？</summary>
	private WearItem? HitTest(WearItem it, Vector2 canvasPos)
	{
		if (it.Tex is null)
			return null;
		var g = WearGeometry(it);
		Vector2 p = ToDollPoint(canvasPos);
		float px = (p.X - g.Left) / g.Width;
		float py = (p.Y - g.TopY) / g.Height;
		if (px < 0f || px > 1f || py < 0f || py > 1f)
			return null;

		// 注意：这里必须和 ApplyWear 一样先「按包围盒宽度归一化」再用，
		// WearGeometry 给的 Cen/Span 是娃娃贴图像素，而 px 是 0~1 的比例。
		float w = Mathf.Lerp(g.SpanTop, g.SpanBot, py) / g.Width;
		float c = (Mathf.Lerp(g.CenTop, g.CenBot, py) - g.Left) / g.Width;
		float su = 0.5f + (px - c) / Mathf.Max(w, 0.0001f);
		if (su < 0f || su > 1f)
			return null;

		// 源贴图坐标 → 取那一像素的 alpha
		var img = SolidMaskOf(it);
		int x = Mathf.Clamp((int)(it.Src.Position.X + su * it.Src.Size.X), 0, img.GetWidth() - 1);
		int y = Mathf.Clamp((int)(it.Src.Position.Y + py * it.Src.Size.Y), 0, img.GetHeight() - 1);
		return img.GetPixel(x, y).A > 0.15f ? it : null;
	}

	// ================= 装饰贴纸：贴 / 选 / 拖 / 删 =================

	/// <summary>贴一张新装饰上去，落在 <see cref="DecoSpots"/> 里轮到的那个位置，并直接选中它。</summary>
	private void AddSticker(int type)
	{
		if (type < 0 || type >= _decos.Count)
			return;
		if (_stickers.Count >= MaxStickers)
		{
			ShowToast("贴纸太多啦，先删掉几个吧");
			return;
		}

		var node = new Sprite2D
		{
			Texture = _decos[type].Tex,
			ZIndex = ZDeco,
		};
		_character.AddChild(node);

		var st = new Sticker
		{
			Type = type,
			Node = node,
			Pos = DecoSpots[_stickerSpot % DecoSpots.Length],
		};
		_stickerSpot++;
		_stickers.Add(st);
		ApplySticker(st);
		SelectSticker(st);
		PlayDing();
	}

	/// <summary>把一张贴纸的位置 / 缩放 / 旋转写进节点（本地坐标是画布像素，所以要乘 DollScale）。</summary>
	private void ApplySticker(Sticker st)
	{
		st.Node.Position = st.Pos * DollScale;
		st.Node.Scale = Vector2.One * (DecoBaseSize / DecoTextureSize * st.UserScale * DollScale);
		st.Node.Rotation = Mathf.DegToRad(st.Deg);
		// 选中的那张还要把高亮外框一起挪过去（外框看着贴纸走、跟着一起转）
		if (ReferenceEquals(st, _selectedSticker))
		{
			_decoFrame.Position = st.Node.Position;
			_decoFrame.Scale = st.Node.Scale;
			_decoFrame.Rotation = st.Node.Rotation;
		}
	}

	/// <summary>选中 / 取消选中一张贴纸：外框和「大小 / 旋转 / 删掉」都跟着换。</summary>
	private void SelectSticker(Sticker? st)
	{
		_selectedSticker = st;
		_decoFrame.Visible = st != null;
		if (st != null)
		{
			ApplySticker(st);   // 顺手把外框摆到那张贴纸上
			// 同层时按节点顺序画，外框要排在所有贴纸之后才盖得住它们
			_character.MoveChild(_decoFrame, _character.GetChildCount() - 1);
		}
		RefreshAdjustRows();
		RefreshStripHighlight();
	}

	/// <summary>把选中的那张贴纸撕下来。</summary>
	private void DeleteSticker()
	{
		if (_selectedSticker == null)
			return;
		var st = _selectedSticker;
		_stickers.Remove(st);
		st.Node.QueueFree();
		SelectSticker(null);
		PlayDing();
	}

	/// <summary>把所有贴纸清掉（开局和「重置」都走这里）。</summary>
	private void ClearStickers()
	{
		foreach (var st in _stickers)
			st.Node.QueueFree();
		_stickers.Clear();
		_stickerSpot = 0;
		SelectSticker(null);
	}

	/// <summary>
	/// 戳中了哪张贴纸？按贴图 alpha 抠形状判，不能按包围盒——
	/// 蝴蝶结的四个角、气球球体以外那一大圈，都是透明的。
	/// 从最后贴上的那张往前找：后贴的压在上面，先撞到它。
	/// </summary>
	private Sticker? HitSticker(Vector2 canvasPos)
	{
		Vector2 p = ToDollPoint(canvasPos);
		for (int i = _stickers.Count - 1; i >= 0; i--)
		{
			var st = _stickers[i];
			Vector2 local = p - st.Pos;
			// 反着转 -Deg，把点转回这张贴纸自己的坐标系
			float rad = -Mathf.DegToRad(st.Deg);
			float cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
			var q = new Vector2(local.X * cs - local.Y * sn, local.X * sn + local.Y * cs);

			float half = DecoBaseSize * st.UserScale * 0.5f;
			if (half <= 1f)
				continue;
			int x = Mathf.FloorToInt((q.X / half * 0.5f + 0.5f) * DecoTextureSize);
			int y = Mathf.FloorToInt((q.Y / half * 0.5f + 0.5f) * DecoTextureSize);
			if (x < 0 || x >= DecoTextureSize || y < 0 || y >= DecoTextureSize)
				continue;
			if (_decos[st.Type].Mask[y * DecoTextureSize + x])
				return st;
		}
		return null;
	}

	/// <summary>
	/// 穿戴物的目标几何（娃娃贴图像素）：先把「大小」缩放套上去（锚点 = 上沿中心，
	/// 这样领口/拱顶不动、裙摆往下长），再加上拖动的位移。
	/// 摆放和命中判定都走这一份，免得两处算错一处。
	/// </summary>
	private readonly struct WearGeom
	{
		public readonly float TopY, BotY, CenTop, CenBot, SpanTop, SpanBot, Left, Width;
		public readonly float Height;

		public WearGeom(float topY, float botY, float cenTop, float cenBot, float spanTop, float spanBot, float left, float width)
		{
			TopY = topY; BotY = botY; CenTop = cenTop; CenBot = cenBot;
			SpanTop = spanTop; SpanBot = spanBot; Left = left; Width = width;
			Height = Mathf.Max(botY - topY, 1f);
		}
	}

	private static WearGeom WearGeometry(WearItem it)
	{
		float s = Mathf.Clamp(it.UserScale, MinUserScale, MaxUserScale);
		float ax = it.TopCenterX, ay = it.TopY;   // 锚点 = 上沿中心
		float cenTop = ax + it.Drag.X;
		float cenBot = ax + (it.BotCenterX - ax) * s + it.Drag.X;
		float spanTop = it.TopWidth * s;
		float spanBot = it.BotWidth * s;
		float topY = ay + it.Drag.Y;
		float botY = ay + (it.BotY - ay) * s + it.Drag.Y;

		float left = Mathf.Min(cenTop - spanTop * 0.5f, cenBot - spanBot * 0.5f);
		float right = Mathf.Max(cenTop + spanTop * 0.5f, cenBot + spanBot * 0.5f);
		return new WearGeom(topY, botY, cenTop, cenBot, spanTop, spanBot, left, right - left);
	}

	private void OnResetPressed()
	{
		PlayPop(_resetButton);
		ApplyInitialLook(silent: false);
		ShowToast("已恢复初始造型");
	}

	/// <summary>回到贴纸游戏选择页。</summary>
	private void OnHomePressed()
	{
		PlayPop(_homeButton);
		GetTree().ChangeSceneToFile(ScenePaths.StickerSelect);
	}

	/// <summary>
	/// 回到初始造型：蓝心裙 + 不戴发箍 + 原色头发 + 无唇彩 + 冷白肤色 + 原色袜子 + 画纸背景，
	/// 顺带把拖过的位移和改过的大小都归位。开局和「重置」都走这里。
	/// </summary>
	private void ApplyInitialLook(bool silent = true)
	{
		foreach (var it in _dresses)
		{
			it.Drag = Vector2.Zero;
			it.UserScale = 1f;
		}
		foreach (var it in _headbands)
		{
			it.Drag = Vector2.Zero;
			it.UserScale = 1f;
		}

		ApplyDress(InitialDress, silent: true);
		ApplyHeadband(InitialHeadband, silent: true);
		ApplyHairColor(InitialHair, silent: true);
		ApplyLipColor(InitialLip, silent: true);
		ApplySkinTone(InitialSkin, silent: true);
		ApplySockColor(InitialSock, silent: true);
		ApplyBackground(InitialBg, silent: true);
		ClearStickers();     // 贴上去的装饰也一并撕掉
		RefreshAdjustRows();

		if (!silent)
		{
			PlayDing();
			RefreshStripHighlight();
		}
	}

	// ================= 区域掩码（头发 / 唇彩 / 肤色 / 袜子）=================

	/// <summary>
	/// 生成「区域掩码」贴图：R=肤色、G=头发、B=唇彩、A=袜子。
	///
	/// RGB 三通道已经用满，袜子这块是后来加的，就挪到 alpha 上——这张掩码只被当作普通
	/// uniform 贴图读，它的 alpha 不参与混色输出，所以拿它当第四个区域不会影响画面。
	///
	/// 娃娃是线稿，所有填色都是纸白，光看颜色分不出哪块是头发、哪块是脸，
	/// 所以只能从种子点泛洪把区域划出来。泛洪只在「够亮的像素」上走：
	/// 纸白能通过，铅笔线（亮度低得多）过不去，于是线稿自然成了区域边界。
	/// 把阈值放宽到 200 是为了让区域一直涂到线稿内沿，不留一圈没上色的白边。
	/// </summary>
	private ImageTexture BuildRegionMap()
	{
		var doll = GD.Load<Texture2D>(AssetDir + "girl_doll.png").GetImage();
		int w = doll.GetWidth();
		int h = doll.GetHeight();
		byte[] src = doll.GetData();   // Rgba8：每 4 字节一个像素
		int n = w * h;

		var fill = new bool[n];        // 纸白：泛洪只在这上面走
		var passable = new bool[n];    // 连抗锯齿边一起，用来往外扩
		for (int i = 0; i < n; i++)
		{
			if (src[i * 4 + 3] <= 128)
				continue;
			float lum = (src[i * 4] * 0.299f + src[i * 4 + 1] * 0.587f + src[i * 4 + 2] * 0.114f) / 255f;
			fill[i] = lum >= 250f / 255f;
			passable[i] = lum >= 200f / 255f;
		}

		// 先到先得：袜子 → 肤色 → 头发 → 唇彩。
		// 袜子必须排在肤色前面：那块脚踝以下的区域跟左右腿是连着的，晚泛就会被腿吃掉。
		var assign = new byte[n];      // 0=不涂 1=肤色 2=头发 3=唇彩 4=袜子
		_maskSock = FloodRegion(assign, fill, passable, SockSeeds, 4, w, h, "袜子");
		_maskSkin = FloodRegion(assign, fill, passable, SkinSeeds, 1, w, h, "肤色");
		_maskHair = FloodRegion(assign, fill, passable, HairSeeds, 2, w, h, "头发");
		_maskLip = FloodRegion(assign, fill, passable, LipSeeds, 3, w, h, "唇彩");

		var outPx = new byte[n * 4];
		for (int i = 0; i < n; i++)
		{
			// 非袜子区域的 alpha 一律 0；袜子区域用 alpha 当第四个通道
			if (assign[i] == 1) { outPx[i * 4] = 255; outPx[i * 4 + 3] = 0; }
			else if (assign[i] == 2) { outPx[i * 4 + 1] = 255; outPx[i * 4 + 3] = 0; }
			else if (assign[i] == 3) { outPx[i * 4 + 2] = 255; outPx[i * 4 + 3] = 0; }
			else if (assign[i] == 4) { outPx[i * 4 + 3] = 255; }
		}
		// 一次性把打包好的像素做成贴图（C# 侧没有单参数版的 SetData）
		var map = Image.CreateFromData(w, h, false, Image.Format.Rgba8, outPx);
		GD.Print($"[HandDrawn] region map: skin={_maskSkin} hair={_maskHair} lip={_maskLip} sock={_maskSock}");
		return ImageTexture.CreateFromImage(map);
	}

	/// <summary>
	/// 从若干种子点圈出一块区域：先在「纸白」上严格泛洪（穿不过线稿），
	/// 再把结果往外扩 <see cref="GrowSteps"/> 步去吃掉线稿的抗锯齿边。
	///
	/// 为什么要分两步：直接按放宽的阈值泛洪，遇到某条偏淡的线就会顺着抗锯齿漏到隔壁区域
	/// （实测过，脸的种子会一路漏进头发里）；而严格泛洪 + 有步数上限的外扩，
	/// 最坏也只是多涂几像素，不会整块串味。外扩 3 步刚好够顶到线稿内沿。
	/// </summary>
	private static int FloodRegion(byte[] assign, bool[] fill, bool[] passable, Vector2I[] seeds,
		byte id, int w, int h, string name)
	{
		const int GrowSteps = 3;
		int total = 0;
		foreach (var seed in seeds)
		{
			int start = SeedIndex(seed, fill, w, h);
			if (start < 0)
			{
				GD.PushError($"[HandDrawn] {name}种子 ({seed.X},{seed.Y}) 附近没有纸白");
				continue;
			}
			if (assign[start] != 0)
			{
				GD.PushError($"[HandDrawn] {name}种子 ({seed.X},{seed.Y}) 落在别的区域里了（id={assign[start]}），换个点");
				continue;
			}

			// ① 严格泛洪（顺便把区域像素收起来，外扩时当出发点）
			var region = new List<int>();
			var stack = new Stack<int>();
			assign[start] = id;
			stack.Push(start);
			while (stack.Count > 0)
			{
				int i = stack.Pop();
				region.Add(i);
				int x = i % w;
				int y = i / w;
				if (x > 0) PushIfFree(assign, fill, i - 1, id, stack);
				if (x < w - 1) PushIfFree(assign, fill, i + 1, id, stack);
				if (y > 0) PushIfFree(assign, fill, i - w, id, stack);
				if (y < h - 1) PushIfFree(assign, fill, i + w, id, stack);
			}

			// ② 有上限地外扩到抗锯齿边
			int area = region.Count;
			var frontier = region;
			for (int step = 0; step < GrowSteps && frontier.Count > 0; step++)
			{
				var next = new List<int>();
				foreach (int i in frontier)
				{
					int x = i % w;
					int y = i / w;
					if (x > 0) GrowInto(assign, passable, i - 1, id, next);
					if (x < w - 1) GrowInto(assign, passable, i + 1, id, next);
					if (y > 0) GrowInto(assign, passable, i - w, id, next);
					if (y < h - 1) GrowInto(assign, passable, i + w, id, next);
				}
				area += next.Count;
				frontier = next;
			}

			total += area;
		}
		return total;
	}

	private static void GrowInto(byte[] assign, bool[] passable, int i, byte id, List<int> next)
	{
		if (!passable[i] || assign[i] != 0)
			return;
		assign[i] = id;
		next.Add(i);
	}

	private static void PushIfFree(byte[] assign, bool[] passable, int i, byte id, Stack<int> stack)
	{
		if (!passable[i] || assign[i] != 0)
			return;
		assign[i] = id;
		stack.Push(i);
	}

	/// <summary>
	/// 找到种子的落点。种子是照分块标色图挑的「区域内部一点」，
	/// 但质心偶尔会压在线上，所以这里在附近找一圈，取最近的可涂像素。
	/// </summary>
	private static int SeedIndex(Vector2I seed, bool[] passable, int w, int h)
	{
		for (int r = 0; r <= 6; r++)
		{
			for (int dy = -r; dy <= r; dy++)
			{
				for (int dx = -r; dx <= r; dx++)
				{
					if (r > 0 && Mathf.Abs(dx) != r && Mathf.Abs(dy) != r)
						continue;   // 只看这一圈的边框
					int x = seed.X + dx;
					int y = seed.Y + dy;
					if (x < 0 || x >= w || y < 0 || y >= h)
						continue;
					int i = y * w + x;
					if (passable[i])
						return i;
				}
			}
		}
		return -1;
	}

	// ================= 拍照 =================

	/// <summary>
	/// 拍照。取的是「上一帧画完的结果」，所以要先藏掉 UI、等两帧再抓，
	/// 否则照片里会带上顶栏按钮和上一句提示。
	/// </summary>
	private async void OnPhotoPressed()
	{
		PlayPop(_photoButton);
		PlayDing();

		var vp = GetViewport();
		bool topBarWasVisible = _topBar.Visible;
		bool toastWasVisible = _toast.Visible;
		_topBar.Visible = false;
		_toast.Visible = false;

		// 等两帧：第一帧画完「没有 UI」的画面，第二帧才保证拿得到它
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		var img = vp.GetTexture().GetImage();

		_topBar.Visible = topBarWasVisible;
		_toast.Visible = toastWasVisible;

		float bottom = Size.Y - PanelHeight;
		var canvasRect = new Rect2(0, 0, Size.X, bottom);
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
		string path = $"user://screenshots/hand_{stamp}_{_photoCounter}.png";
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
		if (node is not Control c)
			return;
		c.PivotOffset = c.Size * 0.5f;
		var tw = CreateTween();
		tw.TweenProperty(c, "scale", new Vector2(0.9f, 0.9f), 0.06);
		tw.TweenProperty(c, "scale", Vector2.One, 0.16)
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

	/// <summary>开局教一次玩法（自测模式下不弹）。</summary>
	private async Task ShowHintAsync()
	{
		await Wait(1.0);
		ShowToast("换裙子、戴发箍，还能贴装饰贴纸");
	}

	// ================= 程序化背景生成 =================
	//
	// 和 StickerGame 同一套纪律：Image.FillRect 是「直接写像素」，不做 alpha 混合。
	// 想画一层半透明的东西，必须自己先算好混合结果、再写不透明的颜色——
	// 只要写进去的 alpha != 1，那一块就真的变成半透明像素，游戏清屏色会透出来。
	// 所以花纹一律走 FillRow：先在底色上把花纹色调好，再写不透明像素。

	// ================= 装饰贴纸（代码画的卡通贴纸）=================
	//
	// 素材里没有装饰件（只有娃娃 / 3 条裙子 / 1 个发箍），所以这六种小玩意儿是代码画的。
	// 画法：在 3 倍大的画布上按「形状方程」硬边填色（像素只有「是 / 不是」两种），
	// 缩回 256 时做 3×3 盒式平均，边缘就自然带了抗锯齿——比手工算覆盖率简单得多。
	// 每个部件都是「先画一圈放大的深色描边、再画本体」，就有卡通贴纸那种粗黑边。

	/// <summary>画一张贴纸用的画布：归一化坐标 u,v ∈ [-1,1]，原点在中心，v 向下。</summary>
	private sealed class PaintCanvas
	{
		private readonly int _ss;     // 超采样倍数
		private readonly int _n;      // 超采样画布边长
		private readonly int _out;    // 最终贴图边长
		private readonly byte[] _px;  // 超采样画布（Rgba8，硬边，不做混合）

		public PaintCanvas(int outSize, int ss)
		{
			_ss = ss;
			_out = outSize;
			_n = outSize * ss;
			_px = new byte[_n * _n * 4];
		}

		/// <summary>把 inside 返回 true 的像素统统写成颜色 c（硬边，不混合）。</summary>
		public void Fill(System.Func<float, float, bool> inside, Color c)
		{
			byte r = (byte)(Mathf.Clamp(c.R, 0f, 1f) * 255f + 0.5f);
			byte g = (byte)(Mathf.Clamp(c.G, 0f, 1f) * 255f + 0.5f);
			byte b = (byte)(Mathf.Clamp(c.B, 0f, 1f) * 255f + 0.5f);
			byte a = (byte)(Mathf.Clamp(c.A, 0f, 1f) * 255f + 0.5f);
			for (int y = 0; y < _n; y++)
			{
				float v = (y + 0.5f) / _n * 2f - 1f;
				int row = y * _n * 4;
				for (int x = 0; x < _n; x++)
				{
					float u = (x + 0.5f) / _n * 2f - 1f;
					if (!inside(u, v))
						continue;
					int i = row + x * 4;
					_px[i] = r; _px[i + 1] = g; _px[i + 2] = b; _px[i + 3] = a;
				}
			}
		}

		/// <summary>一个部件：先铺一圈放大 grow 倍的描边色，再把本体盖上去。</summary>
		public void Part(System.Func<float, float, bool> inside, Color fill, float grow = 1.08f)
		{
			Fill(Grown(inside, grow), DecoInk);
			Fill(inside, fill);
		}

		/// <summary>把形状整体放大 k 倍（同一坐标上判断，等于把图形撑大）。</summary>
		private static System.Func<float, float, bool> Grown(System.Func<float, float, bool> f, float k)
			=> (u, v) => f(u / k, v / k);

		/// <summary>缩回目标尺寸，顺便产出「这一像素实不实」的命中表（alpha > 0.35）。</summary>
		public (Image Img, bool[] Mask) Finish()
		{
			int s = _ss;
			var outPx = new byte[_out * _out * 4];
			var mask = new bool[_out * _out];
			float total = s * s;
			for (int y = 0; y < _out; y++)
			{
				for (int x = 0; x < _out; x++)
				{
					// 按预乘累加：半透明边缘的 rgb 要按 alpha 加权，否则边缘会掺进一圈黑
					float sr = 0f, sg = 0f, sb = 0f, sa = 0f;
					for (int dy = 0; dy < s; dy++)
					{
						int rowBase = (y * s + dy) * _n * 4;
						for (int dx = 0; dx < s; dx++)
						{
							int i = rowBase + (x * s + dx) * 4;
							float a = _px[i + 3] / 255f;
							sr += _px[i] / 255f * a;
							sg += _px[i + 1] / 255f * a;
							sb += _px[i + 2] / 255f * a;
							sa += a;
						}
					}
					float outA = sa / total;
					int o = (y * _out + x) * 4;
					if (sa > 0f)
					{
						outPx[o] = (byte)(sr / sa * 255f + 0.5f);
						outPx[o + 1] = (byte)(sg / sa * 255f + 0.5f);
						outPx[o + 2] = (byte)(sb / sa * 255f + 0.5f);
					}
					outPx[o + 3] = (byte)(outA * 255f + 0.5f);
					mask[y * _out + x] = outA > 0.35f;
				}
			}
			return (Image.CreateFromData(_out, _out, false, Image.Format.Rgba8, outPx), mask);
		}
	}

	// ---- 形状方程（都是 u,v ∈ [-1,1] 里的「点在里面吗」）----

	private static System.Func<float, float, bool> Circle(float cx, float cy, float r)
		=> Ellipse(cx, cy, r, r);

	private static System.Func<float, float, bool> Ellipse(float cx, float cy, float rx, float ry)
		=> (u, v) =>
		{
			float dx = (u - cx) / rx, dy = (v - cy) / ry;
			return dx * dx + dy * dy <= 1f;
		};

	/// <summary>转过角度的椭圆（蝴蝶结的两片环要靠它「斜」过来）。</summary>
	private static System.Func<float, float, bool> RotEllipse(float cx, float cy, float rx, float ry, float deg)
	{
		float a = Mathf.DegToRad(deg), ca = Mathf.Cos(a), sa = Mathf.Sin(a);
		return (u, v) =>
		{
			float dx = u - cx, dy = v - cy;
			float lx = dx * ca + dy * sa, ly = -dx * sa + dy * ca;
			return lx * lx / (rx * rx) + ly * ly / (ry * ry) <= 1f;
		};
	}

	private static System.Func<float, float, bool> Tri(Vector2 a, Vector2 b, Vector2 c)
		=> (u, v) =>
		{
			float d1 = (u - b.X) * (a.Y - b.Y) - (a.X - b.X) * (v - b.Y);
			float d2 = (u - c.X) * (b.Y - c.Y) - (b.X - c.X) * (v - c.Y);
			float d3 = (u - a.X) * (c.Y - a.Y) - (c.X - a.X) * (v - a.Y);
			bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
			bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
			return !(neg && pos);
		};

	/// <summary>凸多边形（顶点按顺序给）。凹的形状要用别的办法拼，见 <see cref="Star"/>。</summary>
	private static System.Func<float, float, bool> Poly(params Vector2[] pts)
		=> (u, v) =>
		{
			bool neg = false, pos = false;
			for (int i = 0; i < pts.Length; i++)
			{
				Vector2 a = pts[i], b = pts[(i + 1) % pts.Length];
				float d = (u - a.X) * (b.Y - a.Y) - (b.X - a.X) * (v - a.Y);
				if (d < 0f) neg = true;
				if (d > 0f) pos = true;
				if (neg && pos) return false;
			}
			return true;
		};

	/// <summary>
	/// 五角星是凹的，不能用一个多边形判——拆成「中心 + 每个角的两条边」共 10 个三角形取并集。
	/// 第一个角朝正上方（v 轴向下，所以起始角取 -90°）。
	/// </summary>
	private static System.Func<float, float, bool> Star(float outer, float inner, int points = 5)
	{
		var tris = new List<System.Func<float, float, bool>>();
		for (int i = 0; i < points; i++)
		{
			float a0 = -Mathf.Pi / 2f + i * Mathf.Tau / points;
			float a1 = a0 + Mathf.Tau / (points * 2f);
			float a2 = a0 + Mathf.Tau / points;
			var p0 = new Vector2(Mathf.Cos(a0) * outer, Mathf.Sin(a0) * outer);
			var p1 = new Vector2(Mathf.Cos(a1) * inner, Mathf.Sin(a1) * inner);
			var p2 = new Vector2(Mathf.Cos(a2) * outer, Mathf.Sin(a2) * outer);
			tris.Add(Tri(Vector2.Zero, p0, p1));
			tris.Add(Tri(Vector2.Zero, p1, p2));
		}
		return (u, v) =>
		{
			foreach (var t in tris)
				if (t(u, v))
					return true;
			return false;
		};
	}

	/// <summary>
	/// 爱心用隐式方程 (x²+y²-1)³ - x²y³ ≤ 0：v 轴向下，所以取 y = -v（心尖自然落在下方）。
	/// cy 是往上抬一点的偏移——这个形状的重心偏上方，不抬的话贴纸看着「没居中」。
	/// </summary>
	private static System.Func<float, float, bool> Heart(float r, float cy)
		=> (u, v) =>
		{
			float x = u / r, y = -(v - cy) / r;
			float t = x * x + y * y - 1f;
			return t * t * t - x * x * y * y * y <= 0f;
		};

	private void BuildDecoTextures()
	{
		AddDeco("蝴蝶结", DrawBow);
		AddDeco("小星星", DrawStar);
		AddDeco("爱心", DrawHeart);
		AddDeco("小蛋糕", DrawCupcake);
		AddDeco("气球", DrawBalloon);
		AddDeco("小花", DrawFlower);
	}

	private void AddDeco(string label, System.Action<PaintCanvas> paint)
	{
		var canvas = new PaintCanvas(DecoTextureSize, DecoSuperSample);
		paint(canvas);
		var (img, mask) = canvas.Finish();
		_decos.Add(new DecoItem { Label = label, Tex = ImageTexture.CreateFromImage(img), Mask = mask });
	}

	/// <summary>蝴蝶结：两条飘带 + 左右两片环 + 中间的结。</summary>
	private static void DrawBow(PaintCanvas c)
	{
		var fill = new Color("#ff8fb3");
		// 飘带先画，压在环的下面
		c.Part(Poly(new(-0.10f, -0.14f), new(-0.46f, 0.30f), new(-0.40f, 0.60f), new(-0.04f, 0.34f)), fill);
		c.Part(Poly(new(0.10f, -0.14f), new(0.46f, 0.30f), new(0.40f, 0.60f), new(0.04f, 0.34f)), fill);
		c.Part(RotEllipse(-0.50f, -0.28f, 0.36f, 0.28f, -16f), fill);
		c.Part(RotEllipse(0.50f, -0.28f, 0.36f, 0.28f, 16f), fill);
		c.Part(Circle(0f, -0.26f, 0.22f), fill);
	}

	private static void DrawStar(PaintCanvas c)
		=> c.Part(Star(0.92f, 0.42f), new Color("#ffcf4d"));

	private static void DrawHeart(PaintCanvas c)
		=> c.Part(Heart(0.70f, 0.09f), new Color("#ff6b8b"));

	/// <summary>小蛋糕：底下的纸杯 + 三团奶油 + 一颗樱桃。</summary>
	private static void DrawCupcake(PaintCanvas c)
	{
		c.Part(Poly(new(-0.54f, 0.10f), new(0.54f, 0.10f), new(0.40f, 0.78f), new(-0.40f, 0.78f)),
			new Color("#f2a96a"));
		var cream = new Color("#ffb9cd");
		c.Part(Circle(-0.32f, -0.10f, 0.30f), cream);
		c.Part(Circle(0.32f, -0.10f, 0.30f), cream);
		c.Part(Circle(0f, -0.34f, 0.36f), cream);
		c.Part(Circle(0f, -0.56f, 0.22f), cream);
		c.Part(Circle(0f, -0.76f, 0.12f), new Color("#ff5a6e"));
	}

	/// <summary>气球：一个蛋形的球 + 下面那个小三角 + 一根飘下来的线 + 一处高光。</summary>
	private static void DrawBalloon(PaintCanvas c)
	{
		c.Part(Ellipse(0f, -0.16f, 0.60f, 0.70f), new Color("#7fc4ef"));
		c.Part(Tri(new(-0.10f, 0.50f), new(0.10f, 0.50f), new(0f, 0.66f)), new Color("#6aa9d8"));
		c.Fill(Poly(new(-0.035f, 0.62f), new(0.035f, 0.62f), new(0.18f, 0.90f), new(0.11f, 0.90f)),
			new Color("#8b8b9a"));
		// 高光用不透明的浅蓝：这里是「直接写像素」，写半透明会在气球上开出一个洞
		c.Fill(Ellipse(-0.22f, -0.44f, 0.13f, 0.18f), new Color("#dcf0ff"));
	}

	/// <summary>小花：五片花瓣围一圈 + 黄色的花心。</summary>
	private static void DrawFlower(PaintCanvas c)
	{
		var petal = new Color("#ffa8d0");
		for (int i = 0; i < 5; i++)
		{
			float a = -Mathf.Pi / 2f + i * Mathf.Tau / 5f;
			float cx = Mathf.Cos(a) * 0.42f, cy = Mathf.Sin(a) * 0.42f;
			c.Part(RotEllipse(cx, cy, 0.36f, 0.30f, Mathf.RadToDeg(a)), petal);
		}
		c.Part(Circle(0f, 0f, 0.24f), new Color("#ffd75e"));
	}

	/// <summary>
	/// 选中贴纸时套在外面的虚线圈。画成圆（而不是方框）是有意的：
	/// 贴纸可以任意旋转，圆转起来不会歪，方框一转就跟贴纸错开。
	/// </summary>
	private static ImageTexture MakeDecoFrameTexture()
	{
		var c = new PaintCanvas(DecoTextureSize, 2);
		c.Fill((u, v) =>
		{
			float d = Mathf.Sqrt(u * u + v * v);
			if (d < 0.86f || d > 0.98f)
				return false;
			// 每 22.5° 断一次，看起来像虚线
			float ang = Mathf.Atan2(v, u) + Mathf.Pi;
			return (int)(ang / (Mathf.Pi / 8f)) % 2 == 0;
		}, new Color("#ff9f43"));
		var (img, _) = c.Finish();
		return ImageTexture.CreateFromImage(img);
	}

	private ImageTexture MakeBackgroundTexture(BgItem bg, int w = 720, int h = 1280)
	{
		var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
		for (int y = 0; y < h; y++)
			FillRow(img, y, 0, w - 1, SkyAt(bg, y, h));

		switch (bg.Pattern)
		{
			case "grid":
				DrawGrid(img, bg, w, h, 72);
				break;
			case "dots":
				DrawDots(img, bg, w, h, 110, 14);
				break;
			case "clouds":
				DrawClouds(img, bg, w, h);
				break;
			case "stripes":
				DrawStripes(img, bg, w, h, 96, 34);
				break;
		}
		return ImageTexture.CreateFromImage(img);
	}

	/// <summary>第 y 行的底色。渐变只跟 y 有关，所以每行是个纯色，画花纹时可以直接拿它当混合底色。</summary>
	private static Color SkyAt(BgItem bg, int y, int h)
	{
		float t = h > 1 ? y / (float)(h - 1) : 0f;
		return bg.Top.Lerp(bg.Bottom, t);
	}

	/// <summary>在一行里写一段像素，越界自动裁剪，并强制写成不透明。</summary>
	private static void FillRow(Image img, int y, int x0, int x1, Color c)
	{
		if (y < 0 || y >= img.GetHeight())
			return;
		x0 = Mathf.Max(x0, 0);
		x1 = Mathf.Min(x1, img.GetWidth() - 1);
		if (x1 < x0)
			return;
		img.FillRect(new Rect2I(x0, y, x1 - x0 + 1, 1), new Color(c.R, c.G, c.B, 1f));
	}

	/// <summary>方格线（练习本的感觉）：横线整行铺，竖线逐行按该行底色混色。</summary>
	private static void DrawGrid(Image img, BgItem bg, int w, int h, int step)
	{
		for (int y = 0; y < h; y += step)
			for (int t = 0; t < 2; t++)
				FillRow(img, y + t, 0, w - 1, SkyAt(bg, y + t, h).Lerp(bg.Ink, 0.6f));

		for (int y = 0; y < h; y++)
		{
			var c = SkyAt(bg, y, h).Lerp(bg.Ink, 0.6f);
			for (int x = 0; x < w; x += step)
				FillRow(img, y, x, x + 1, c);
		}
	}

	/// <summary>错位排列的圆点（交点阵列）。</summary>
	private static void DrawDots(Image img, BgItem bg, int w, int h, int step, int r)
	{
		for (int cy = step / 2, row = 0; cy < h + step; cy += step, row++)
		{
			int offset = row % 2 == 0 ? 0 : step / 2;
			for (int cx = step / 2 + offset; cx < w + step; cx += step)
			{
				for (int dy = -r; dy <= r; dy++)
				{
					int y = cy + dy;
					if (y < 0 || y >= h)
						continue;
					int dx = (int)Mathf.Sqrt(Mathf.Max(0f, (float)r * r - (float)dy * dy));
					FillRow(img, y, cx - dx, cx + dx, SkyAt(bg, y, h).Lerp(bg.Ink, 0.9f));
				}
			}
		}
	}

	private static void DrawClouds(Image img, BgItem bg, int w, int h)
	{
		DrawCloud(img, bg, w, h, w * 0.22f, h * 0.16f, w * 0.055f);
		DrawCloud(img, bg, w, h, w * 0.78f, h * 0.29f, w * 0.048f);
		DrawCloud(img, bg, w, h, w * 0.34f, h * 0.62f, w * 0.050f);
		DrawCloud(img, bg, w, h, w * 0.85f, h * 0.80f, w * 0.045f);
	}

	private static void DrawCloud(Image img, BgItem bg, int w, int h, float cx, float cy, float r)
	{
		int ir = (int)r;
		void Puff(int ox, int oy, int rr)
		{
			for (int dy = -rr; dy <= rr; dy++)
			{
				int y = (int)cy + oy + dy;
				if (y < 0 || y >= h)
					continue;
				int dx = (int)Mathf.Sqrt(Mathf.Max(0f, (float)rr * rr - (float)dy * dy));
				FillRow(img, y, (int)cx + ox - dx, (int)cx + ox + dx, SkyAt(bg, y, h).Lerp(bg.Ink, 0.85f));
			}
		}
		Puff(-ir, 0, (int)(r * 0.7f));
		Puff(ir, 0, (int)(r * 0.7f));
		Puff(0, -ir / 3, (int)(r * 0.95f));
	}

	/// <summary>斜条纹：每行按 (x + y) 取模决定这一段是不是条纹。</summary>
	private static void DrawStripes(Image img, BgItem bg, int w, int h, int period, int width)
	{
		for (int y = 0; y < h; y++)
		{
			var c = SkyAt(bg, y, h).Lerp(bg.Ink, 0.42f);
			int shift = y % period;
			for (int x = -shift; x < w; x += period)
				FillRow(img, y, x, x + width - 1, c);
		}
	}

	// ================= 自测模式 =================

	private async Task RunSelfTestAsync()
	{
		try
		{
			await RunSelfTestBodyAsync();
		}
		catch (System.Exception e)
		{
			// 自测是 fire-and-forget 协程，异常会被 Task 静默吞掉（表现为日志戛然而止、进程不退出）
			GD.Print($"[SELFTEST] CRASHED: {e}");
			GetTree().Quit();
		}
	}

	private async Task RunSelfTestBodyAsync()
	{
		GD.Print("[SELFTEST] begin (hand)");
		await Wait(0.4);

		int fails = 0;

		// ① 纹理加载
		if (_doll.Texture == null) { GD.PushError("[SELFTEST] doll texture null"); fails++; }
		foreach (var d in _dresses)
			if (d.Tex != null && GD.Load<Texture2D>(d.Tex) == null) { GD.PushError($"[SELFTEST] dress {d.Label} null"); fails++; }
		foreach (var hb in _headbands)
			if (hb.Tex != null && GD.Load<Texture2D>(hb.Tex) == null) { GD.PushError($"[SELFTEST] headband {hb.Label} null"); fails++; }
		GD.Print("[SELFTEST] textures: ok");

		// ② 背景完全不透明（FillRect 不做 alpha 混合，写错 alpha 会在背景上留下真正的透明窟窿）
		int translucent = 0;
		foreach (var tex in _bgTextures)
		{
			byte[] data = tex.GetImage().GetData(); // Format.Rgba8 → 每 4 字节一个像素
			for (int i = 3; i < data.Length; i += 4)
				if (data[i] != 255)
					translucent++;
		}
		if (translucent > 0)
		{
			GD.PushError($"[SELFTEST] background has {translucent} translucent pixels");
			fails++;
		}
		else
			GD.Print("[SELFTEST] background opaque: ok");

		// ③ 初始造型：蓝心裙 + 不戴发箍 + 原色头发 + 无唇彩 + 冷白 + 原色袜子 + 画纸
		bool initialOk = _dressIndex == InitialDress && _headbandIndex == InitialHeadband &&
						 _hairIndex == InitialHair && _lipIndex == InitialLip &&
						 _skinIndex == InitialSkin && _sockIndex == InitialSock && _bgIndex == InitialBg;
		GD.Print($"[SELFTEST] initial look: dress={_dressIndex} headband={_headbandIndex} " +
				 $"hair={_hairIndex} lip={_lipIndex} skin={_skinIndex} sock={_sockIndex} bg={_bgIndex} -> {initialOk}");
		if (!initialOk) fails++;

		// ④ 逐分类切换（每个都要真的落到 Sprite 上）
		for (int i = 0; i < _dresses.Count; i++) { ApplyDress(i); await Wait(0.12); }
		for (int i = 0; i < _headbands.Count; i++) { ApplyHeadband(i); await Wait(0.12); }
		for (int i = 0; i < _bgs.Count; i++) { ApplyBackground(i); await Wait(0.12); }

		// ⑤ 「不穿」档位的高亮也要跟着走
		SelectCategory(0, silent: true);
		ApplyDress(0);
		await Wait(0.1);
		bool stripOffOk = StripButtonLit(0);
		GD.Print($"[SELFTEST] \"不穿\" button highlighted: {stripOffOk}");
		if (!stripOffOk) fails++;

		// ⑥ 穿上每件裙子/发箍之后，Sprite 必须真的有纹理、并且落在娃娃身上（和娃娃的包围盒相交）
		foreach (var (idx, item) in Indexed(_dresses))
		{
			if (item.Tex is null) continue;
			ApplyDress(idx);
			await Wait(0.06);
			bool ok = _dress.Visible && _dress.Texture != null && WearsOnDoll(_dress);
			GD.Print($"[SELFTEST] dress \"{item.Label}\" visible={_dress.Visible} on-doll={WearsOnDoll(_dress)} -> {ok}");
			if (!ok) fails++;
		}
		foreach (var (idx, item) in Indexed(_headbands))
		{
			if (item.Tex is null) continue;
			ApplyHeadband(idx);
			await Wait(0.06);
			bool ok = _headband.Visible && _headband.Texture != null && WearsOnDoll(_headband);
			GD.Print($"[SELFTEST] headband \"{item.Label}\" visible={_headband.Visible} on-doll={WearsOnDoll(_headband)} -> {ok}");
			if (!ok) fails++;
		}

		// ⑦ 区域掩码：四块区域都得泛出东西来，而且量级要对。
		//    （种子点或泛洪阈值写错 → 区域会漏到隔壁或几乎为空，这里能立刻抓住）
		bool maskSizesOk = _maskSkin > 15000 && _maskSkin < 45000 &&
						   _maskHair > 20000 && _maskHair < 55000 &&
						   _maskLip > 250 && _maskLip < 2500 &&
						   _maskSock > 1200 && _maskSock < 6000;
		GD.Print($"[SELFTEST] region mask sizes: skin={_maskSkin} hair={_maskHair} " +
				 $"lip={_maskLip} sock={_maskSock} -> {maskSizesOk}");
		if (!maskSizesOk) fails++;

		// ⑦b 掩码通道打包：已知区域内部点的通道必须对得上（R=肤色 / G=头发 / B=唇彩 / A=袜子）。
		//     袜子是塞在 alpha 上的，这条专门守它——打包写成「所有像素 alpha=255」是最容易犯的错。
		var maskImg = ((Texture2D)((ShaderMaterial)_doll.Material).GetShaderParameter("region_map")).GetImage();
		Color sockPx = maskImg.GetPixel(153, 583);   // 袜子内部点
		Color skinPx = maskImg.GetPixel(148, 137);   // 脸
		Color hairPx = maskImg.GetPixel(230, 153);   // 右侧头发
		Color lipPx = maskImg.GetPixel(161, 162);    // 嘴唇
		bool maskPackOk = sockPx.A > 0.9f && sockPx.R < 0.1f && sockPx.G < 0.1f && sockPx.B < 0.1f &&
						  skinPx.R > 0.9f && skinPx.G < 0.1f && skinPx.B < 0.1f && skinPx.A < 0.1f &&
						  hairPx.G > 0.9f && hairPx.R < 0.1f && hairPx.B < 0.1f && hairPx.A < 0.1f &&
						  lipPx.B > 0.9f && lipPx.R < 0.1f && lipPx.G < 0.1f && lipPx.A < 0.1f;
		GD.Print($"[SELFTEST] region map packing: sock={sockPx} skin={skinPx} hair={hairPx} lip={lipPx} -> {maskPackOk}");
		if (!maskPackOk) fails++;

		// ⑦c 装饰贴纸的贴图：六种都得画出来，而且形状要「居中、没顶到画布边」。
		//     形状方程写歪 / 描边放大后超出画布，都会从质心和四角暴露出来。
		foreach (var d in _decos)
		{
			int filled = 0;
			double cx = 0, cy = 0;
			for (int y = 0; y < DecoTextureSize; y++)
			{
				for (int x = 0; x < DecoTextureSize; x++)
				{
					if (!d.Mask[y * DecoTextureSize + x])
						continue;
					filled++;
					cx += x;
					cy += y;
				}
			}
			double fx = filled > 0 ? cx / filled / DecoTextureSize : -1;
			double fy = filled > 0 ? cy / filled / DecoTextureSize : -1;
			bool cornersEmpty = !d.Mask[0] && !d.Mask[DecoTextureSize - 1] &&
								!d.Mask[(DecoTextureSize - 1) * DecoTextureSize] &&
								!d.Mask[DecoTextureSize * DecoTextureSize - 1];
			bool ok = d.Tex != null && d.Tex.GetWidth() == DecoTextureSize &&
					  filled > DecoTextureSize * DecoTextureSize / 12 &&
					  filled < DecoTextureSize * DecoTextureSize * 3 / 4 &&
					  cornersEmpty &&
					  Mathf.Abs((float)fx - 0.5f) < 0.12f && Mathf.Abs((float)fy - 0.5f) < 0.12f;
			GD.Print($"[SELFTEST] deco \"{d.Label}\": filled={filled} centroid=({fx:0.00},{fy:0.00}) " +
					 $"corners-empty={cornersEmpty} -> {ok}");
			if (!ok) fails++;
		}

		// ⑧ 头发 / 唇彩 / 肤色 / 袜子：逐档切换，并且着色器参数要真的跟着变
		var dollMat = (ShaderMaterial)_doll.Material;
		for (int i = 0; i < _hairColors.Count; i++) { ApplyHairColor(i); await Wait(0.05); }
		for (int i = 0; i < _lipColors.Count; i++) { ApplyLipColor(i); await Wait(0.05); }
		for (int i = 0; i < _skinTones.Count; i++) { ApplySkinTone(i); await Wait(0.05); }
		for (int i = 0; i < _sockColors.Count; i++) { ApplySockColor(i); await Wait(0.05); }

		ApplyHairColor(2);   // 栗棕
		ApplyLipColor(2);    // 正红
		ApplySkinTone(2);    // 暖黄
		ApplySockColor(2);   // 天蓝
		await Wait(0.05);
		Vector3 skinParam = (Vector3)dollMat.GetShaderParameter("skin_tint");
		Vector3 hairParam = (Vector3)dollMat.GetShaderParameter("hair_tint");
		Vector3 lipParam = (Vector3)dollMat.GetShaderParameter("lip_tint");
		Vector3 sockParam = (Vector3)dollMat.GetShaderParameter("sock_tint");
		bool tintOk = skinParam.IsEqualApprox(ToVec3(_skinTones[2].Tone)) &&
					  hairParam.IsEqualApprox(ToVec3(_hairColors[2].Tone)) &&
					  lipParam.IsEqualApprox(ToVec3(_lipColors[2].Tone)) &&
					  sockParam.IsEqualApprox(ToVec3(_sockColors[2].Tone));
		GD.Print($"[SELFTEST] tints -> skin={skinParam} hair={hairParam} lip={lipParam} sock={sockParam} -> {tintOk}");
		if (!tintOk) fails++;

		// ⑨ 拖动：先找一个「真的戳在裙子上」的点，再找一个「在同一包围盒里但落在透明处」的点
		ApplyDress(1);           // 蓝心裙（包围盒 760 宽，裙体只有 300 左右，最能体现按 alpha 判定）
		ApplyHeadband(0);
		await Wait(0.05);
		var blueDress = _dresses[1];
		Vector2 hitPos = FindHitPoint(blueDress);
		var g0 = WearGeometry(blueDress);
		Vector2 missPos = FindMissPoint(blueDress) ?? new Vector2(-9999f, -9999f);

		bool grabOk = BeginDrag(hitPos);
		bool emptyMiss = !BeginDrag(missPos);
		MoveDrag(hitPos + new Vector2(60, -40));
		bool dropOk = EndDrag();
		float moved = blueDress.Drag.Length();
		bool dragOk = grabOk && dropOk && emptyMiss && moved > 40f;
		GD.Print($"[SELFTEST] drag probe: hit={hitPos} miss={missPos} stage={_stageRect} center={_center}");
		GD.Print($"[SELFTEST] drag: grab={grabOk} transparent-miss={emptyMiss} drop={dropOk} moved={moved:0.#}px -> {dragOk}");
		if (!dragOk) fails++;

		// ⑩ 大小：0.5~2，超范围要夹住；几何要跟着变
		blueDress.UserScale = 1.6f;
		var gBig = WearGeometry(blueDress);
		blueDress.UserScale = 3f;                       // 超上限 → 夹到 2
		var gClampHi = WearGeometry(blueDress);
		blueDress.UserScale = 0.1f;                     // 超下限 → 夹到 0.5
		var gClampLo = WearGeometry(blueDress);
		blueDress.UserScale = 1f;
		bool scaleOk = gBig.Height > g0.Height * 1.5f &&
					   Mathf.Abs(gClampHi.Height - g0.Height * 2f) < 1f &&
					   Mathf.Abs(gClampLo.Height - g0.Height * 0.5f) < 1f;
		GD.Print($"[SELFTEST] scale: base h={g0.Height:0.#} x1.6 h={gBig.Height:0.#} " +
				 $"clamp[0.5,2] h={gClampLo.Height:0.#}/{gClampHi.Height:0.#} -> {scaleOk}");
		if (!scaleOk) fails++;

		// 截一张「拖动 + 放大」后的样子，确认两个操作叠在一起也对。
		// 缩放走滑块这条真实路径（顺带验证 ValueChanged -> 换装 这条链路）
		SelectCategory(0, silent: true);
		blueDress.Drag = new Vector2(-30, 26);
		_scaleSlider.Value = 1.45;
		await Wait(0.3);
		SaveShot("hand_dragscale");
		blueDress.Drag = Vector2.Zero;
		ApplyInitialLook(silent: true);
		await Wait(0.2);

		// ⑩b 装饰贴纸：贴一张 → 选中 → 拖 → 转 → 缩放 → 删，一路走「玩家真会走」的那几条路径
		SelectCategory(CatDeco, silent: true);
		await Wait(0.05);
		bool decoStripOk = _decos.Count == 6 && _stripButtons.Count == _decos.Count &&
						   !_rotationRow.Visible && !_scaleRow.Visible && _stickers.Count == 0;
		GD.Print($"[SELFTEST] deco strip: types={_decos.Count} buttons={_stripButtons.Count} " +
				 $"rows-hidden={!_rotationRow.Visible} -> {decoStripOk}");
		if (!decoStripOk) fails++;

		AddSticker(1);                       // 小星星（中心一定是实心的，好当命中探针）
		var star = _stickers[^1];
		Vector2 spot0 = star.Pos;
		bool addOk = _stickers.Count == 1 && star.Node.Texture == _decos[1].Tex &&
					 star.Node.ZIndex == ZDeco && star.Node.Visible &&
					 ReferenceEquals(_selectedSticker, star) && _decoFrame.Visible &&
					 _rotationRow.Visible && _scaleRow.Visible && StripButtonLit(1);
		GD.Print($"[SELFTEST] add sticker: count={_stickers.Count} selected={ReferenceEquals(_selectedSticker, star)} " +
				 $"frame={_decoFrame.Visible} rotation-row={_rotationRow.Visible} -> {addOk}");
		if (!addOk) fails++;

		// 命中判定：贴在正中心要判中（贴纸按 alpha 抠形状，中心是实心的），
		// 挪到贴纸外一个「在同一包围盒里但透明」的地方要判不中
		Vector2 onStar = _center + star.Pos * DollScale;
		Vector2 offStar = _center + (star.Pos + new Vector2(DecoBaseSize * 1.2f, 0f)) * DollScale;
		bool hitOk = ReferenceEquals(HitSticker(onStar), star) && HitSticker(offStar) == null;
		GD.Print($"[SELFTEST] sticker hit: on={HitSticker(onStar) != null} off={HitSticker(offStar) != null} -> {hitOk}");
		if (!hitOk) fails++;

		bool decoGrab = BeginDrag(onStar);
		MoveDrag(onStar + new Vector2(40f, 30f));
		bool decoDrop = EndDrag();
		float decoMoved = (star.Pos - spot0).Length();
		bool decoDragOk = decoGrab && decoDrop && decoMoved > 20f;
		GD.Print($"[SELFTEST] sticker drag: grab={decoGrab} drop={decoDrop} moved={decoMoved:0.#}px -> {decoDragOk}");
		if (!decoDragOk) fails++;

		_rotationSlider.Value = 35;
		await Wait(0.05);
		bool rotOk = Mathf.IsEqualApprox(star.Deg, 35f) &&
					 Mathf.IsEqualApprox(star.Node.Rotation, Mathf.DegToRad(35f)) &&
					 _rotationValue.Text == "35°" && _decoFrame.Visible;
		GD.Print($"[SELFTEST] sticker rotate: deg={star.Deg} node={star.Node.Rotation:0.###} text=\"{_rotationValue.Text}\" -> {rotOk}");
		if (!rotOk) fails++;

		_scaleSlider.Value = 1.5;
		await Wait(0.05);
		float wantScale = DecoBaseSize / DecoTextureSize * 1.5f * DollScale;
		bool decoScaleOk = Mathf.IsEqualApprox(star.UserScale, 1.5f) &&
						   Mathf.IsEqualApprox(star.Node.Scale.X, wantScale) &&
						   _scaleValue.Text == "1.50×";
		GD.Print($"[SELFTEST] sticker scale: scale={star.UserScale:0.##} node={star.Node.Scale.X:0.####} " +
				 $"want={wantScale:0.####} -> {decoScaleOk}");
		if (!decoScaleOk) fails++;

		await Wait(0.3);
		SaveShot("hand_deco_rot");           // 歪着贴 + 放大后的样子

		_deleteButton.EmitSignal(BaseButton.SignalName.Pressed);
		await Wait(0.1);
		bool decoDelOk = _stickers.Count == 0 && _selectedSticker == null &&
						 !_decoFrame.Visible && !_rotationRow.Visible && !_scaleRow.Visible;
		GD.Print($"[SELFTEST] sticker delete: count={_stickers.Count} rows-hidden={!_rotationRow.Visible} -> {decoDelOk}");
		if (!decoDelOk) fails++;

		// 六种各截一张（贴图长得对不对要肉眼过一眼），最后留两张给「重置」那条断言用
		for (int i = 0; i < _decos.Count; i++)
		{
			ClearStickers();
			AddSticker(i);
			await Wait(0.25);
			SaveShot($"hand_deco{i}");
		}
		ClearStickers();
		SelectCategory(CatDeco, silent: true);
		AddSticker(0);
		AddSticker(3);
		await Wait(0.25);
		SaveShot("hand_deco_all");

		// ⑪ 顶栏：三颗按钮都在、标签对、而且都接上了回调
		bool topBarOk = _homeButton.Visible && _resetButton.Visible && _photoButton.Visible &&
						_homeButton.Text == "返回" && _resetButton.Text == "重置" && _photoButton.Text == "拍照" &&
						_homeButton.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count > 0 &&
						_resetButton.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count > 0 &&
						_photoButton.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count > 0;
		GD.Print($"[SELFTEST] top bar: \"{_homeButton.Text}\" / \"{_resetButton.Text}\" / \"{_photoButton.Text}\" -> {topBarOk}");
		if (!topBarOk) fails++;

		// ⑧ 拍照
		OnPhotoPressed();
		await Wait(0.8);

		// ⑫ 重置：应该回到初始造型（颜色 / 拖动 / 大小都要归位），贴上去的装饰也要全清掉
		int stickersBeforeReset = _stickers.Count;
		ApplyInitialLook(silent: false);
		await Wait(0.4);
		bool resetOk = _dressIndex == InitialDress && _headbandIndex == InitialHeadband &&
					   _hairIndex == InitialHair && _lipIndex == InitialLip &&
					   _skinIndex == InitialSkin && _sockIndex == InitialSock && _bgIndex == InitialBg &&
					   _dress.Visible && _dress.Texture != null && !_headband.Visible &&
					   blueDress.Drag == Vector2.Zero && Mathf.IsEqualApprox(blueDress.UserScale, 1f) &&
					   stickersBeforeReset == 2 && _stickers.Count == 0 &&
					   _selectedSticker == null && !_decoFrame.Visible;
		GD.Print($"[SELFTEST] after reset: dress={_dressIndex} headband={_headbandIndex} hair={_hairIndex} " +
				 $"lip={_lipIndex} skin={_skinIndex} sock={_sockIndex} bg={_bgIndex} drag={blueDress.Drag} " +
				 $"scale={blueDress.UserScale:0.##} stickers={stickersBeforeReset}->{_stickers.Count} -> {resetOk}");
		if (!resetOk) fails++;

		// ⑩ 截图文件真的写出来了
		string dir = ProjectSettings.GlobalizePath("user://screenshots");
		bool found = System.IO.Directory.Exists(dir) &&
					 System.IO.Directory.GetFiles(dir, "hand_*.png").Length > 0;
		GD.Print($"[SELFTEST] photo exists: {found}");
		if (!found) fails++;

		// ⑪ 三套造型各截一张整屏图，方便肉眼核对裙子/发箍的对齐
		for (int i = 0; i < _dresses.Count; i++)
		{
			ApplyDress(i);
			if (i > 0) ApplyHeadband(1);
			await Wait(0.25);
			SaveShot($"hand_look{i}");
		}
		// 只戴发箍（不穿裙子）：单独截一张，方便核对发箍和头/脸的相对位置
		ApplyDress(0);
		ApplyHeadband(1);
		await Wait(0.25);
		SaveShot("hand_band");

		ApplyDress(InitialDress);
		ApplyHeadband(1);
		await Wait(0.25);
		SaveShot("hand_final");

		// 四个背景各截一张，核对花纹（点/云/条纹）画得对不对
		for (int i = 0; i < _bgs.Count; i++)
		{
			ApplyBackground(i);
			await Wait(0.25);
			SaveShot($"hand_bg{i}");
		}
		ApplyBackground(InitialBg);

		// 每个分类标签各截一张，核对物品栏的缩略图排得对不对（8 个标签要一行放得下）
		for (int i = 0; i < 8; i++)
		{
			SelectCategory(i, silent: true);
			await Wait(0.25);
			SaveShot($"hand_tab{i}");
		}
		SelectCategory(0, silent: true);

		// 每种发色 / 唇色 / 肤色各截一张，核对区域有没有涂到线稿外面、线稿还看不看得清
		SelectCategory(2, silent: true);
		for (int i = 0; i < _hairColors.Count; i++)
		{
			ApplyHairColor(i);
			await Wait(0.2);
			SaveShot($"hand_hair{i}");
		}
		ApplyHairColor(InitialHair);
		SelectCategory(3, silent: true);
		for (int i = 0; i < _lipColors.Count; i++)
		{
			ApplyLipColor(i);
			await Wait(0.2);
			SaveShot($"hand_lip{i}");
		}
		ApplyLipColor(InitialLip);
		SelectCategory(4, silent: true);
		for (int i = 0; i < _skinTones.Count; i++)
		{
			ApplySkinTone(i);
			await Wait(0.2);
			SaveShot($"hand_skin{i}");
		}
		ApplySkinTone(InitialSkin);
		SelectCategory(5, silent: true);
		for (int i = 0; i < _sockColors.Count; i++)
		{
			ApplySockColor(i);
			await Wait(0.2);
			SaveShot($"hand_sock{i}");
		}
		ApplySockColor(InitialSock);

		// 再截一张「区域掩码可视化」：把四种区域染成极端色，一眼就能看出划得对不对
		ApplyHairColor(2);   // 栗棕
		ApplyLipColor(2);    // 正红
		ApplySkinTone(2);    // 暖黄
		ApplySockColor(2);   // 天蓝
		SelectCategory(0, silent: true);
		await Wait(0.25);
		SaveShot("hand_tint");
		ApplyInitialLook(silent: true);

		// 物品栏左右箭头：内容超一屏时能翻页、到端自动变灰（手机触屏拖不动那根细滚动条）
		if (!await _stripPager.SelfTestAsync())
			fails++;

		GD.Print($"[SELFTEST] so far: {(fails == 0 ? "ok" : fails + " problem(s)")}");

		// ⑫ 点「返回」要真的回到贴纸选择页。
		//    换场景会把本场景连同这里的协程一起销毁，所以：
		//      a) 校验交给挂在树根上、不随场景销毁的见证节点；
		//      b) 这行之后**不能再 await**（await 的续体会跟着本场景一起死掉）。
		var witness = new BackWitness(fails == 0);
		GetTree().Root.AddChild(witness);
		_ = witness.VerifyAsync();
		_homeButton.EmitSignal(BaseButton.SignalName.Pressed);
	}

	/// <summary>给列表带上下标，方便自测里同时拿到「第几档」和「是哪件」。</summary>
	private static IEnumerable<(int Index, T Item)> Indexed<T>(List<T> list)
	{
		for (int i = 0; i < list.Count; i++)
			yield return (i, list[i]);
	}

	/// <summary>
	/// 自测用：在穿戴物的目标梯形里扫一圈，找出第一个「真的戳到实心处」的画布坐标。
	/// 不写死坐标是为了以后改数值表也不用跟着改测试。
	/// </summary>
	private Vector2 FindHitPoint(WearItem it)
	{
		var found = ScanWear(it, wantHit: true);
		if (found == null)
			GD.PushError($"[SELFTEST] 在 \"{it.Label}\" 的梯形里找不到任何实心点");
		return found ?? new Vector2(-9999f, -9999f);
	}

	/// <summary>
	/// 自测用：找一个「落在梯形里、但那一处是透明的」画布坐标，
	/// 用来验证命中判定真的按 alpha 抠了形状（而不是照着包围盒一把抓）。
	/// 必须在舞台范围内，否则测到的是「点在面板上」这条别的分支。
	/// </summary>
	private Vector2? FindMissPoint(WearItem it)
	{
		var found = ScanWear(it, wantHit: false);
		if (found == null)
			GD.PushError($"[SELFTEST] \"{it.Label}\" 的梯形里找不到透明处，命中判定没法验证");
		return found;
	}

	private Vector2? ScanWear(WearItem it, bool wantHit)
	{
		var g = WearGeometry(it);
		for (float py = 0.02f; py <= 0.98f; py += 0.02f)
		{
			for (float px = 0.02f; px <= 0.98f; px += 0.02f)
			{
				var p = new Vector2(g.Left + px * g.Width, g.TopY + py * g.Height);
				var canvas = _center + p * DollScale;
				if (!_stageRect.HasPoint(canvas))
					continue;
				if ((HitTest(it, canvas) != null) == wantHit)
					return canvas;
			}
		}
		return null;
	}

	/// <summary>
	/// 这件穿戴物是否真的「穿在娃娃身上」：拿两者的全局包围盒相交来判定。
	/// 能抓到两类低级错——偏移写飞了（摆到屏幕外 / 和娃娃完全不相交）、缩放写成了 0。
	/// 注意 <see cref="Sprite2D.GetRect"/> 给的是**未经缩放**的本地矩形，
	/// 必须用全局变换换算，否则摆错位置也照样「相交」。
	/// </summary>
	private bool WearsOnDoll(Sprite2D sp)
	{
		Rect2 worn = GlobalRectOf(sp);
		if (worn.Size.X <= 8f || worn.Size.Y <= 8f)
			return false;
		return worn.Intersects(GlobalRectOf(_doll));
	}

	private static Rect2 GlobalRectOf(Sprite2D sp)
	{
		if (sp.Texture == null)
			return new Rect2();
		Rect2 r = sp.GetRect();
		Transform2D xf = sp.GetGlobalTransform();
		return new Rect2(xf * r.Position, xf.BasisXform(r.Size)).Abs();
	}

	/// <summary>整屏截图（自测用；游戏的「拍照」只截角色框，看不见顶栏）。</summary>
	private string SaveShot(string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
		string stamp = Time.GetDatetimeStringFromSystem()
			.Replace("-", "").Replace(":", "").Replace(" ", "_");
		string path = $"user://screenshots/{tag}_{stamp}.png";
		var err = img.SavePng(path);
		GD.Print($"[HandDrawn] screenshot({tag}) err={err} -> {ProjectSettings.GlobalizePath(path)}");
		return ProjectSettings.GlobalizePath(path);
	}

	/// <summary>自测辅助：物品栏第 index 个按钮是否处于高亮态。</summary>
	private bool StripButtonLit(int index)
	{
		if (index < 0 || index >= _stripButtons.Count)
		{
			GD.PushError($"[SELFTEST] strip index {index} out of range");
			return false;
		}
		return _stripButtons[index].Modulate == HighlightTint;
	}

	private async Task Wait(double seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
	}

	/// <summary>
	/// 「换场景之后」的见证者。本场景一换就被销毁，它自己的协程会跟着死，
	/// 所以把校验放到挂在树根上的这个节点里。
	/// 用「监听 NodeAdded」而不是「逐帧看 CurrentScene」：换过来的场景内部还有一大堆子节点，
	/// 监听 NodeAdded 能直接拿到「挂到树根上的那个场景」，不必逐帧采样。
	/// </summary>
	private sealed partial class BackWitness : Node
	{
		private readonly bool _passedBefore;
		private readonly List<string> _rootScenes = new();

		public BackWitness(bool passedBefore)
		{
			_passedBefore = passedBefore;
		}

		public override void _Ready()
		{
			GetTree().NodeAdded += OnNodeAdded;
		}

		/// <summary>只收「直接挂到树根上的场景」，场景内部那堆子节点不算。</summary>
		private void OnNodeAdded(Node node)
		{
			if (node.GetParent() == GetTree().Root && node is not BackWitness)
				_rootScenes.Add(node.Name);
		}

		public async Task VerifyAsync()
		{
			await ToSignal(GetTree().CreateTimer(0.9), SceneTreeTimer.SignalName.Timeout);
			GetTree().NodeAdded -= OnNodeAdded;

			bool ok = _rootScenes.Contains("StickerSelect");
			bool passed = ok && _passedBefore;
			GD.Print($"[SELFTEST] click \"返回\" -> scenes loaded: [{string.Join(", ", _rootScenes)}] (expect StickerSelect) : {ok}");
			GD.Print(passed ? "[SELFTEST] PASSED" : "[SELFTEST] FAILED");

			// 说明：现在「返回」去的是贴纸选择页；选择页见 flag 不是 "select"，不会再自动往下走，
			// 所以这里就是最后一轮，稍等一拍后直接退出进程。
			await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
			GetTree().Quit();
		}
	}
}