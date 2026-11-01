// ============================================================================
//  Chat.cs  —  Chat：基于 DeepSeek API 的桌面对话小工具
// ----------------------------------------------------------------------------
//  设计目标：
//    1. 目标电脑可能没有 Python，因此用 C# WinForms 编译成单个原生 exe；
//    2. 编译目标为 .NET Framework 4.0（Win10 自带 4.6+ 可直接运行，
//       Win7 装了 .NET4 也能跑），因此：
//         * 不使用 async/await（4.0 运行时没有 AsyncTaskMethodBuilder）
//         * 不使用 System.Text.Json（那是 .NET Core 的东西）
//         * 不使用 C# 6+ 语法（字符串插值、?.、nameof 等）
//         * JSON 只做“手写请求体 + JavaScriptSerializer 解析小响应块”
//    3. API Key 从 exe 同目录的 apikey.txt 读取（明文，不上传、不外发）；
//    4. 输入框内 Ctrl+V 直接粘贴剪贴板里的图片，随问题一起发给 deepseek-flash。
//
//  编译：双击同目录 build.bat（调用系统自带 csc.exe）
//  自检：Chat.exe --selftest   结果写入 selftest.log
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// ============================================================================
//  程序集信息：资源管理器“详细信息”、任务栏提示里显示的名字就是这里
// ============================================================================
[assembly: AssemblyTitle("Chat")]
[assembly: AssemblyProduct("Chat")]
[assembly: AssemblyDescription("Chat —— 基于 DeepSeek API 的对话小工具")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace DeepSeekChat
{
    // ========================================================================
    //  1. 对话消息模型
    // ========================================================================
    internal sealed class Msg
    {
        public string Role;      // user / assistant
        public string Text;      // 文本内容（图片消息里也带一段文字，可为空串）
        public string ImageUrl;  // data:image/xxx;base64,....  仅 user 且带图时非空

        public Msg(string role, string text, string imageUrl)
        {
            Role = role;
            Text = text == null ? "" : text;
            ImageUrl = imageUrl;
        }
    }

    // ========================================================================
    //  2. 配置（来自 config.txt，缺项用这里的默认值）
    // ========================================================================
    internal sealed class Config
    {
        /// <summary>内置的默认系统提示词（config.txt 和提示词.txt 都没写时用它）。</summary>
        public const string DefaultSystemPrompt = "你是一个乐于助人的中文助手。回答直给结论、条理清晰、不啰嗦。";

        public string Model = "deepseek-flash";                        // 支持图片的模型
        public string BaseUrl = "https://api.deepseek.com";            // 官方地址
        public int TimeoutSec = 180;                                   // 单次请求超时（秒）
        public string SystemPrompt = DefaultSystemPrompt;
        public bool Thinking = false;                                  // 深度思考默认关闭（快）
        public string ReasoningEffort = "high";                        // low / high / max
        public int KeepImageTurns = 1;                                 // 历史里最多保留几条带图消息
        public int MaxHistoryMessages = 40;                             // 历史条数上限，防止越聊越贵
        public string Proxy = "";                                      // 例：http://127.0.0.1:7897，留空=系统默认
        public int WindowWidth = 0;                                     // 窗口默认宽（逻辑像素，0=内置默认）
        public int WindowHeight = 0;                                    // 窗口默认高（逻辑像素，0=内置默认）
        public string Ui = "compact";                                   // compact=极简两行界面；full=完整界面
        public bool ReinforcePrompt = false;                            // 每轮把系统提示词再贴一次到问题后面
        public string ScreenshotHotkey = "Ctrl+Alt+Q";                  // 截图快捷键（全局热键）
        public string WindowHotkey = "Ctrl+Alt+C";                      // 显示/隐藏窗口的全局热键
        public bool ShowTaskbarIcon = true;                             // 是否在任务栏显示图标（关掉后靠托盘图标找回窗口）
        public string CloseButton = "hide";                             // 点叉号的行为：hide=隐藏到后台；exit=直接退出
        public int WindowOpacity = 100;                                 // 窗口不透明度的百分比（20~100）

        /// <summary>是否使用极简界面（只有一行输出 + 一行输入 + 有图时的“移除图片”）。</summary>
        public bool CompactUI
        {
            get { return !string.Equals(Ui, "full", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>复制一份，用于“本轮用哪套参数”的快照，避免改动污染全局配置。</summary>
        public Config Clone()
        {
            return (Config)this.MemberwiseClone();
        }

        /// <summary>当前模型能不能吃图片（deepseek-flash 支持，deepseek-v4-pro 不支持）。</summary>
        public bool ModelSupportsImage
        {
            get
            {
                string m = (Model == null ? "" : Model).ToLowerInvariant();
                return m.IndexOf("flash") >= 0 || m.IndexOf("vision") >= 0 || m.IndexOf("vl") >= 0;
            }
        }
    }

    // ========================================================================
    //  3. 通用小工具：JSON 转义 / 文件读取 / 配置与 Key 解析
    // ========================================================================
    internal static class Util
    {
        /// <summary>把字符串转义成 JSON 字面量内部内容（不含首尾引号）。</summary>
        public static string JsonEscape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>去掉 UTF-8 BOM 与首尾空白，并剥掉可能出现的一层引号。</summary>
        public static string Clean(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1).Trim();
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') ||
                                  (s[0] == '\'' && s[s.Length - 1] == '\'')))
                s = s.Substring(1, s.Length - 2).Trim();
            return s;
        }

        /// <summary>
        /// 读文本文件，自动认编码；文件不存在返回 null。
        /// 为什么要自动认：老版记事本（Win10 1903 之前）默认把 txt 存成 ANSI，
        /// 在中文系统上就是 GBK。如果死按 UTF-8 读，用户写的提示词会变成乱码送给模型。
        /// </summary>
        public static string ReadTextFileAuto(string path)
        {
            if (!File.Exists(path)) return null;

            byte[] raw;
            try { raw = File.ReadAllBytes(path); }
            catch { return null; }
            return DecodeBytesAuto(raw);
        }

        /// <summary>字节 → 文本（自动认编码）。抽出来是为了能脱离文件做自检。</summary>
        public static string DecodeBytesAuto(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return "";

            // ① 带 BOM：记事本另存为 UTF-8 / Unicode 都走这里
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
                return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
            if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
                return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
            if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);

            // ② 无 BOM：先用“严格模式”试 UTF-8，不合法就说明不是 UTF-8
            try { return new UTF8Encoding(false, true).GetString(raw); }
            catch (DecoderFallbackException) { }

            // ③ 退回 GBK(936)，再退回系统 ANSI
            try { return Encoding.GetEncoding(936).GetString(raw); }
            catch { return Encoding.Default.GetString(raw); }
        }

        /// <summary>兼容旧调用名。</summary>
        public static string ReadTextFile(string path)
        {
            return ReadTextFileAuto(path);
        }

        /// <summary>
        /// 决定这一轮用哪段系统提示词：提示词文件 &gt; config.txt 的 system_prompt &gt; 内置默认。
        /// 单独抽出来是为了能自检（界面类没法在无窗口环境里跑）。
        /// </summary>
        public static string ResolvePrompt(string configPrompt, string filePrompt, string builtinDefault, out string source)
        {
            string f = Clean(filePrompt);
            if (f.Length > 0) { source = "提示词文件"; return f; }
            string c = Clean(configPrompt);
            if (c.Length > 0) { source = "config.txt"; return c; }
            source = "内置默认";
            return builtinDefault;
        }

        /// <summary>
        /// 裁剪历史对话：最多留 maxMessages 条；只保留最近 keepImageTurns 张图的原始数据，
        /// 更早的图片清掉但保留文字（换成“（原图已省略）”），免得每轮都重传几十 MB。
        /// 抽成静态方法是为了让“最小复现 / 请求体导出 / 自检”用的是同一份逻辑，不会走偏。
        /// </summary>
        public static void TrimHistory(List<Msg> history, int maxMessages, int keepImageTurns)
        {
            if (history == null) return;

            if (maxMessages > 0 && history.Count > maxMessages)
                history.RemoveRange(0, history.Count - maxMessages);

            int keep = Math.Max(0, keepImageTurns);
            int seen = 0;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrEmpty(history[i].ImageUrl)) continue;
                seen++;
                if (seen > keep)
                {
                    history[i].ImageUrl = null;
                    if (history[i].Text.IndexOf("（原图已省略）") < 0)
                        history[i].Text = history[i].Text + "（原图已省略）";
                }
            }
        }

        /// <summary>把多行文本切成行数组（兼容 \r\n / \r / \n）。</summary>
        public static string[] SplitLines(string text)
        {
            if (text == null) return new string[0];
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        /// <summary>
        /// 解析 apikey.txt。兼容以下写法（# 或 // 开头的行视为注释）：
        ///     sk-xxxxxxxx
        ///     apikey = sk-xxxxxxxx
        ///     api_key: "sk-xxxxxxxx"
        /// 优先返回 sk- 开头的行，否则返回第一条有效值。
        /// </summary>
        public static string ParseApiKey(string content)
        {
            string first = null;
            string[] lines = SplitLines(content);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim().TrimStart('\uFEFF');
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith("//")) continue;

                string value = line;
                int eq = line.IndexOf('=');
                int colon = line.IndexOf(':');
                int cut = -1;
                if (eq >= 0 && (colon < 0 || eq < colon)) cut = eq;
                else if (colon >= 0) cut = colon;
                if (cut >= 0) value = line.Substring(cut + 1);

                value = Clean(value);
                if (value.Length == 0) continue;
                if (value.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)) return value;
                if (first == null) first = value;
            }
            return first;
        }

        /// <summary>解析 config.txt 的 key=value 行（忽略注释与空行）。</summary>
        public static Dictionary<string, string> ParseConfig(string content)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = SplitLines(content);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim().TrimStart('\uFEFF');
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith("//")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = Clean(line.Substring(eq + 1));
                map[k] = v;
            }
            return map;
        }

        public static int ParseInt(string s, int def)
        {
            int v;
            if (int.TryParse(Clean(s), out v)) return v;
            return def;
        }

        public static bool ParseBool(string s, bool def)
        {
            string v = Clean(s).ToLowerInvariant();
            if (v == "on" || v == "true" || v == "yes" || v == "1" || v == "开") return true;
            if (v == "off" || v == "false" || v == "no" || v == "0" || v == "关") return false;
            return def;
        }

        /// <summary>
        /// 解析快捷键写法（形如 Ctrl+Alt+Q、Shift+F2）。返回 false 表示写法不认识。
        /// 要求至少带一个修饰键——否则会把普通按键全系统抢走，那是灾难。
        /// </summary>
        public static bool ParseHotkey(string text, out uint modifiers, out uint vk, out string normalized)
        {
            modifiers = 0; vk = 0; normalized = "";
            string s = Clean(text);
            if (s.Length == 0) return false;

            uint mods = 0;
            string key = null;
            string[] parts = s.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) return false;
                string u = p.ToUpperInvariant();
                if (u == "CTRL" || u == "CONTROL") mods |= 0x0002;          // MOD_CONTROL
                else if (u == "ALT") mods |= 0x0001;                         // MOD_ALT
                else if (u == "SHIFT") mods |= 0x0004;                       // MOD_SHIFT
                else if (u == "WIN" || u == "WINDOWS") mods |= 0x0008;       // MOD_WIN
                else
                {
                    if (key != null) return false;    // 出现了两个主键
                    key = u;
                }
            }
            if (key == null) return false;

            uint v = 0;
            if (key.Length == 1)
            {
                char c = key[0];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) v = (uint)c;
                else return false;
            }
            else if (key.Length >= 2 && key[0] == 'F')
            {
                int n;
                if (!int.TryParse(key.Substring(1), out n) || n < 1 || n > 24) return false;
                v = (uint)(0x70 + n - 1);             // VK_F1 = 0x70
            }
            else return false;

            if (mods == 0) return false;              // 必须带修饰键
            modifiers = mods;
            vk = v;
            normalized = BuildHotkeyName(mods, v);
            return true;
        }

        /// <summary>把修饰键 + 主键还原成可读名字，例如 Ctrl+Alt+Q。</summary>
        public static string BuildHotkeyName(uint modifiers, uint vk)
        {
            StringBuilder sb = new StringBuilder();
            if ((modifiers & 0x0002) != 0) sb.Append("Ctrl+");
            if ((modifiers & 0x0001) != 0) sb.Append("Alt+");
            if ((modifiers & 0x0004) != 0) sb.Append("Shift+");
            if ((modifiers & 0x0008) != 0) sb.Append("Win+");
            if (vk >= 0x70 && vk <= 0x87) sb.Append('F').Append(vk - 0x70 + 1);
            else sb.Append((char)vk);
            return sb.ToString();
        }

        /// <summary>把 Key 打码后显示（只露头尾，避免截图泄露）。</summary>
        public static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "(空)";
            if (key.Length <= 12) return key.Substring(0, Math.Min(4, key.Length)) + "****";
            return key.Substring(0, 7) + "****" + key.Substring(key.Length - 4);
        }

        /// <summary>把 config.txt 的键值映射写入 Config 对象。</summary>
        public static void ApplyConfigMap(Config cfg, Dictionary<string, string> map)
        {
            string v;
            if (map.TryGetValue("model", out v) && Clean(v).Length > 0) cfg.Model = Clean(v);
            if (map.TryGetValue("base_url", out v) && Clean(v).Length > 0) cfg.BaseUrl = Clean(v).TrimEnd('/');
            if (map.TryGetValue("timeout_sec", out v)) cfg.TimeoutSec = ParseInt(v, cfg.TimeoutSec);
            if (map.TryGetValue("system_prompt", out v) && Clean(v).Length > 0) cfg.SystemPrompt = Clean(v);
            if (map.TryGetValue("thinking", out v)) cfg.Thinking = ParseBool(v, cfg.Thinking);
            if (map.TryGetValue("reasoning_effort", out v) && Clean(v).Length > 0) cfg.ReasoningEffort = Clean(v);
            if (map.TryGetValue("keep_image_turns", out v)) cfg.KeepImageTurns = ParseInt(v, cfg.KeepImageTurns);
            if (map.TryGetValue("max_history_messages", out v)) cfg.MaxHistoryMessages = ParseInt(v, cfg.MaxHistoryMessages);
            if (map.TryGetValue("proxy", out v)) cfg.Proxy = Clean(v);
            if (map.TryGetValue("window_width", out v)) cfg.WindowWidth = ParseInt(v, cfg.WindowWidth);
            if (map.TryGetValue("window_height", out v)) cfg.WindowHeight = ParseInt(v, cfg.WindowHeight);
            if (map.TryGetValue("ui", out v) && Clean(v).Length > 0) cfg.Ui = Clean(v);
            if (map.TryGetValue("reinforce_prompt", out v)) cfg.ReinforcePrompt = ParseBool(v, cfg.ReinforcePrompt);
            if (map.TryGetValue("screenshot_hotkey", out v) && Clean(v).Length > 0) cfg.ScreenshotHotkey = Clean(v);
            if (map.TryGetValue("window_hotkey", out v) && Clean(v).Length > 0) cfg.WindowHotkey = Clean(v);
            if (map.TryGetValue("show_in_taskbar", out v)) cfg.ShowTaskbarIcon = ParseBool(v, cfg.ShowTaskbarIcon);
            if (map.TryGetValue("close_button", out v) && Clean(v).Length > 0) cfg.CloseButton = Clean(v);
            if (map.TryGetValue("window_opacity", out v)) cfg.WindowOpacity = ParseInt(v, cfg.WindowOpacity);
        }

        /// <summary>
        /// 把配置里的透明度百分比收进可用范围。
        /// 下限 20%：再低窗口就几乎看不见了，等于把自己藏起来还没法点 —— 那是死路。
        /// </summary>
        public static int ClampOpacity(int percent)
        {
            if (percent < 20) return 20;
            if (percent > 100) return 100;
            return percent;
        }
    }

    // ========================================================================
    //  4. 图片处理：缩放 + 编码成 data URL
    //     剪贴板拿到的图常常很大（几十 MB），必须压一压再发：省流量、省钱。
    // ========================================================================
    internal static class ImageCodec
    {
        public const int MaxSide = 1600;                       // 长边上限（像素）
        private const long PngKeepLimit = 1200 * 1024;          // PNG 超过这个大小就转 JPEG

        /// <summary>把任意 Image 转成适合上传的 data URL，并回传一句人类可读的信息。</summary>
        public static string ToDataUrl(Image src, out string info)
        {
            info = "";
            if (src == null) return null;

            Bitmap work = ResizeIfNeeded(src, MaxSide);
            try
            {
                byte[] png = EncodeWith(work, ImageFormat.Png, 0L);
                byte[] bytes;
                string mime;

                if (png != null && png.LongLength <= PngKeepLimit)
                {
                    bytes = png;                       // 截图/文字类：PNG 清晰
                    mime = "image/png";
                }
                else
                {
                    Bitmap flat = FlattenOnWhite(work); // JPEG 不支持透明，铺白底
                    try { bytes = EncodeWith(flat, ImageFormat.Jpeg, 88L); }
                    finally { flat.Dispose(); }
                    mime = "image/jpeg";
                }

                if (bytes == null) return null;
                info = string.Format("{0}×{1}px，{2} {3:F0} KB",
                                     work.Width, work.Height, mime.Substring(6).ToUpperInvariant(),
                                     bytes.LongLength / 1024.0);
                return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
            }
            finally
            {
                if (!ReferenceEquals(work, src)) work.Dispose();
            }
        }

        private static Bitmap ResizeIfNeeded(Image src, int maxSide)
        {
            int w = src.Width, h = src.Height;
            int longSide = Math.Max(w, h);
            if (longSide <= maxSide) return new Bitmap(src);

            double scale = (double)maxSide / longSide;
            int nw = Math.Max(1, (int)Math.Round(w * scale));
            int nh = Math.Max(1, (int)Math.Round(h * scale));

            Bitmap dst = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, nw, nh));
            }
            return dst;
        }

        private static Bitmap FlattenOnWhite(Image src)
        {
            Bitmap dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.Clear(Color.White);
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            }
            return dst;
        }

        private static byte[] EncodeWith(Image img, ImageFormat format, long quality)
        {
            ImageCodecInfo codec = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == format.Guid) { codec = c; break; }
            if (codec == null) return null;

            EncoderParameters ep = null;
            if (quality > 0)
            {
                ep = new EncoderParameters(1);
                // 注意要写全名：Encoder 同时存在于 System.Drawing.Imaging 和 System.Text
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            }
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    if (ep != null) img.Save(ms, codec, ep);
                    else img.Save(ms, format);
                    return ms.ToArray();
                }
            }
            catch { return null; }
            finally { if (ep != null) ep.Dispose(); }
        }
    }

    // ========================================================================
    //  5. DeepSeek 客户端：拼请求体 → POST → 逐行读 SSE → 回调增量文本
    //     全部同步实现，跑在后台线程里，所以 .NET 4.0 也能用。
    // ========================================================================
    internal sealed class DeepSeekClient
    {
        public delegate void DeltaHandler(string reasoning, string content);

        private readonly Config _cfg;
        private readonly string _apiKey;
        private HttpWebRequest _req;
        private volatile bool _canceled;
        private readonly object _lock = new object();

        public DeepSeekClient(Config cfg, string apiKey)
        {
            _cfg = cfg;
            _apiKey = apiKey;
        }

        public bool Canceled { get { return _canceled; } }

        /// <summary>停止本轮请求（用户点“停止”或关闭窗口时调用）。</summary>
        public void Cancel()
        {
            _canceled = true;
            lock (_lock)
            {
                if (_req != null)
                {
                    try { _req.Abort(); } catch { }
                }
            }
        }

        /// <summary>
        /// 发起一轮对话。onDelta 会被多次调用（思考片段 + 回答片段）。
        /// 返回正式的完整回答；思考内容通过 out 回传。异常统一抛出 Exception。
        /// </summary>
        public string Chat(List<Msg> msgs, DeltaHandler onDelta, out string fullReasoning)
        {
            fullReasoning = "";
            if (string.IsNullOrEmpty(_apiKey)) throw new Exception("没有读到 API Key，请检查 apikey.txt。");

            string url = _cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
            byte[] body = Encoding.UTF8.GetBytes(BuildRequestBody(msgs));

            // ---- .NET 4.0 默认只开 SSL3/TLS1.0，必须手动放行 TLS 1.2 ----
            // 768 = TLS1.0，3072 = TLS1.2（老版枚举里没有 Tls12 常量，只能强转）
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(768 | 3072); }
            catch { }
            ServicePointManager.Expect100Continue = false;

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Accept = "text/event-stream";
            req.UserAgent = "DeepSeekDesktopChat/1.0 (WinForms; .NET4.0)";
            req.Timeout = Math.Max(10, _cfg.TimeoutSec) * 1000;
            req.ReadWriteTimeout = Math.Max(10, _cfg.TimeoutSec) * 1000;
            req.Headers.Add("Authorization", "Bearer " + _apiKey);
            req.ContentLength = body.Length;

            if (!string.IsNullOrEmpty(_cfg.Proxy))
            {
                try { req.Proxy = new WebProxy(_cfg.Proxy); }
                catch (Exception ex) { throw new Exception("config.txt 里的 proxy 写错了：" + ex.Message); }
            }

            lock (_lock) { _req = req; }
            try
            {
                using (Stream reqStream = req.GetRequestStream())
                    reqStream.Write(body, 0, body.Length);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string answer = ReadSse(sr, onDelta, ref fullReasoning);
                    return answer;
                }
            }
            catch (WebException wex)
            {
                if (_canceled) throw new Exception("已停止。");
                throw new Exception(DescribeWebException(wex));
            }
            catch (Exception)
            {
                // 点“停止”时 Abort() 抛出的形态不一定是 WebException，这里统一收口
                if (_canceled) throw new Exception("已停止。");
                throw;
            }
            finally
            {
                lock (_lock) { _req = null; }
            }
        }

        /// <summary>逐行解析 SSE。DeepSeek 的流式格式：data: {json} ... data: [DONE]</summary>
        private static string ReadSse(StreamReader sr, DeltaHandler onDelta, ref string reasoning)
        {
            StringBuilder answer = new StringBuilder();
            StringBuilder think = new StringBuilder();
            JavaScriptSerializer js = new JavaScriptSerializer();

            string line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                if (line[0] == ':') continue;                       // SSE 注释/心跳
                if (!line.StartsWith("data:")) continue;

                string payload = line.Substring(5).Trim();
                if (payload.Length == 0) continue;
                if (payload == "[DONE]") break;

                string r = "", c = "";
                if (!TryReadDelta(js, payload, out r, out c)) continue;
                if (r.Length == 0 && c.Length == 0) continue;

                if (r.Length > 0) think.Append(r);
                if (c.Length > 0) answer.Append(c);
                if (onDelta != null) onDelta(r, c);
            }
            reasoning = think.ToString();
            return answer.ToString();
        }

        /// <summary>从单个 SSE 数据块里取出 delta.reasoning_content 与 delta.content。</summary>
        internal static bool TryReadDelta(JavaScriptSerializer js, string payload, out string reasoning, out string content)
        {
            reasoning = ""; content = "";
            try
            {
                Dictionary<string, object> root = js.DeserializeObject(payload) as Dictionary<string, object>;
                if (root == null) return false;

                object errObj;
                if (root.TryGetValue("error", out errObj) && errObj != null)
                {
                    Dictionary<string, object> err = errObj as Dictionary<string, object>;
                    string msg = err != null && err.ContainsKey("message") ? Convert.ToString(err["message"]) : Convert.ToString(errObj);
                    throw new Exception("接口返回错误：" + msg);
                }

                object choicesObj;
                if (!root.TryGetValue("choices", out choicesObj)) return false;
                object[] choices = choicesObj as object[];
                if (choices == null || choices.Length == 0) return false;

                Dictionary<string, object> ch0 = choices[0] as Dictionary<string, object>;
                if (ch0 == null) return false;
                object deltaObj;
                if (!ch0.TryGetValue("delta", out deltaObj)) return false;
                Dictionary<string, object> delta = deltaObj as Dictionary<string, object>;
                if (delta == null) return false;

                object v;
                if (delta.TryGetValue("reasoning_content", out v) && v != null) reasoning = Convert.ToString(v);
                if (delta.TryGetValue("content", out v) && v != null) content = Convert.ToString(v);
                return true;
            }
            catch (Exception)
            {
                throw;   // 让上层把接口报错原样显示出来
            }
        }

        /// <summary>手写请求体：完全掌控 JSON，避免序列化器对超大 base64 的限制。</summary>
        internal string BuildRequestBody(List<Msg> msgs)
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("{\"model\":\"").Append(Util.JsonEscape(_cfg.Model)).Append("\"");
            sb.Append(",\"stream\":true");
            if (_cfg.Thinking)
            {
                sb.Append(",\"thinking\":{\"type\":\"enabled\"}");
                sb.Append(",\"reasoning_effort\":\"").Append(Util.JsonEscape(_cfg.ReasoningEffort)).Append("\"");
            }
            else
            {
                sb.Append(",\"thinking\":{\"type\":\"disabled\"}");
            }

            sb.Append(",\"messages\":[");
            bool firstMsg = true;

            if (!string.IsNullOrEmpty(_cfg.SystemPrompt))
            {
                sb.Append("{\"role\":\"system\",\"content\":\"")
                  .Append(Util.JsonEscape(_cfg.SystemPrompt)).Append("\"}");
                firstMsg = false;
            }

            for (int i = 0; i < msgs.Count; i++)
            {
                Msg m = msgs[i];
                if (!firstMsg) sb.Append(',');
                firstMsg = false;
                sb.Append("{\"role\":\"").Append(Util.JsonEscape(m.Role)).Append("\",\"content\":");

                // reinforce_prompt = on 时，把系统提示词再贴一次到本轮的提问里。
                // 为什么贴在提问里、而不是在末尾再加一条 system 消息：
                // 这样 100% 是标准 OpenAI 兼容格式（末尾插 system 有些服务端会挑），
                // 而且位置最靠近问题，模型对规则的注意力最强。
                string text = m.Text;
                if (_cfg.ReinforcePrompt && i == msgs.Count - 1 && m.Role == "user" &&
                    !string.IsNullOrEmpty(_cfg.SystemPrompt))
                {
                    text = (text == null ? "" : text) +
                           "\r\n\r\n──── 以下是必须始终遵守的设定（每轮重申）────\r\n" +
                           _cfg.SystemPrompt;
                }

                if (!string.IsNullOrEmpty(m.ImageUrl))
                {
                    // 数组形式：先文字后图片。注意图片只允许出现在 user 消息里。
                    sb.Append("[{\"type\":\"text\",\"text\":\"").Append(Util.JsonEscape(text)).Append("\"},");
                    sb.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":\"")
                      .Append(Util.JsonEscape(m.ImageUrl)).Append("\"}}]");
                }
                else
                {
                    sb.Append("\"").Append(Util.JsonEscape(text)).Append("\"");
                }
                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>把 WebException 翻译成人能看懂的提示。</summary>
        private static string DescribeWebException(WebException wex)
        {
            HttpWebResponse resp = wex.Response as HttpWebResponse;
            if (resp == null)
            {
                return "网络请求失败：" + wex.Message +
                       "\r\n（若这台电脑需要代理才能上网，请在 config.txt 里设置 proxy=）";
            }

            string detail = "";
            try
            {
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    detail = sr.ReadToEnd();
            }
            catch { }

            int code = (int)resp.StatusCode;
            string hint;
            switch (code)
            {
                case 401: hint = "API Key 无效或没填，请检查 apikey.txt。"; break;
                case 402: hint = "账户余额不足。"; break;
                case 403: hint = "没有权限访问该模型。"; break;
                case 404: hint = "接口地址不对，检查 config.txt 里的 base_url。"; break;
                case 429: hint = "请求太频繁，稍等几秒再试。"; break;
                default:
                    if (code >= 500) hint = "服务器暂时不可用，过一会儿再试。";
                    else hint = "请求被拒绝（HTTP " + code + "）。";
                    break;
            }
            return string.Format("HTTP {0}：{1}\r\n{2}", code, hint, detail.Trim());
        }
    }

    // ========================================================================
    //  6. 支持“Ctrl+V 直接贴图”的输入框
    //     TextBox 自己处理 Ctrl+V 时不发 WM_KEYDOWN 给我们，所以这里
    //     重写 WndProc 拦截 WM_PASTE，先看剪贴板里有没有图片。
    // ========================================================================
    internal sealed class PasteAwareTextBox : TextBox
    {
        public delegate bool TryPasteImageHandler();

        private const int WM_PASTE = 0x0302;
        private const int WM_MOUSEWHEEL = 0x020A;

        /// <summary>返回 true 表示“这次粘贴已经被我处理掉了，别再走默认文本粘贴”。</summary>
        public TryPasteImageHandler OnPasteImage;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PASTE && OnPasteImage != null)
            {
                bool handled = false;
                try { handled = OnPasteImage(); }
                catch { handled = false; }
                if (handled) return;
            }
            base.WndProc(ref m);
        }
    }

    // ========================================================================
    //  7. 主窗口
    // ========================================================================
    // ========================================================================
    //  6.5 截图选择层
    //      做法：先把整屏抓成一张快照铺满屏幕，再在快照上画淡灰色选择框。
    //      为什么不用“真正透明的窗口”：透明窗口里画不出不透明的选框，
    //      而且鼠标拖动选区还会穿透到底下的窗口去，根本框不住。
    //      用快照看起来就是“只有选框是可见的，其余就是原样的屏幕”。
    // ========================================================================
    internal sealed class CaptureOverlay : Form
    {
        private readonly Bitmap _screen;
        private bool _dragging;
        private Point _start, _current;

        // 自测用：记录这个类在本次进程里被创建/显示过几次（判断流程有没有真的走到选择层）
        internal static int Constructed;
        internal static int Shown;
        internal static int LastSelectionW;
        internal static int LastSelectionH;
        internal static bool LastConfirmed;

        /// <summary>
        /// 自测模式：忽略真实鼠标/键盘，只接受 TestMouse* 钩子。
        /// 为什么需要：自测窗口会出现在用户屏幕上，用户随手一点就会把测试打乱
        /// （实测发生过：用户点了一下，被判成“误触取消”，测试随即报错）。
        /// </summary>
        internal bool TestMode;
        private bool _testInput;

        private bool IgnoreRealInput() { return TestMode && !_testInput; }

        /// <summary>已确认的选区（相对快照左上角）。</summary>
        public Rectangle Selection { get; private set; }
        /// <summary>是否是“框选完成”（false = 用户取消了）。</summary>
        public bool Confirmed { get; private set; }

        public CaptureOverlay(Bitmap screen, Point screenOrigin)
        {
            Constructed++;
            _screen = screen;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            BackColor = Color.Black;
            Bounds = new Rectangle(screenOrigin.X, screenOrigin.Y, screen.Width, screen.Height);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);
        }

        protected override void OnShown(EventArgs e)
        {
            Shown++;
            base.OnShown(e);
        }

        private static Rectangle Normalize(Point a, Point b)
        {
            int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            int w = Math.Abs(a.X - b.X), h = Math.Abs(a.Y - b.Y);
            return new Rectangle(x, y, w, h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (IgnoreRealInput()) return;
            if (e.Button == MouseButtons.Right) { CancelCapture(); return; }
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _start = _current = e.Location;
                Capture = true;                       // 拖出窗口也要继续收到鼠标移动
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IgnoreRealInput()) return;
            if (_dragging) { _current = e.Location; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (IgnoreRealInput()) return;
            if (_dragging && e.Button == MouseButtons.Left)
            {
                _dragging = false;
                Capture = false;
                _current = e.Location;
                Rectangle r = Normalize(_start, _current);
                if (r.Width < 4 || r.Height < 4) { Invalidate(); return; }   // 太小当误触，重新选
                Selection = r;
                Confirmed = true;
                LastConfirmed = true;
                LastSelectionW = r.Width;
                LastSelectionH = r.Height;
                Close();
            }
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (TestMode) return;                       // 自测时别被真实按键打断
            if (e.KeyCode == Keys.Escape) { CancelCapture(); return; }
            base.OnKeyDown(e);
        }

        private void CancelCapture()
        {
            Confirmed = false;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 底色就是抓好的屏幕快照 —— 所以除了选框，用户看到的就是原本的画面
            if (_screen != null) e.Graphics.DrawImageUnscaled(_screen, 0, 0);

            Rectangle sel = Normalize(_start, _current);
            if (_dragging && sel.Width > 0 && sel.Height > 0)
            {
                // 淡灰色选框：内层浅灰(215) + 外层深一点的描边保证在浅色背景上也看得见
                using (Pen outer = new Pen(Color.FromArgb(110, 110, 110), 1f))
                using (Pen inner = new Pen(Color.FromArgb(215, 215, 215), 2f))
                {
                    e.Graphics.DrawRectangle(outer, sel.X, sel.Y, sel.Width, sel.Height);
                    if (sel.Width > 3 && sel.Height > 3)
                        e.Graphics.DrawRectangle(inner, sel.X + 1, sel.Y + 1, sel.Width - 3, sel.Height - 3);
                }

                // 选区尺寸提示（贴着选区左上角显示）
                string size = sel.Width + " × " + sel.Height;
                Size ts = TextRenderer.MeasureText(size, this.Font);
                int bw = ts.Width + 10, bh = ts.Height + 4;
                int bx = sel.X;
                int by = sel.Y - bh - 2;
                if (by < 0) by = sel.Y + 2;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(205, 255, 255, 255)))
                    e.Graphics.FillRectangle(b, bx, by, bw, bh);
                TextRenderer.DrawText(e.Graphics, size, this.Font, new Rectangle(bx, by, bw, bh),
                                      Color.FromArgb(60, 60, 60), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // 顶部操作提示
            string hint = "拖动鼠标框选截图区域　·　Esc / 右键取消";
            Size hs = TextRenderer.MeasureText(hint, this.Font);
            int hw = hs.Width + 24, hh = hs.Height + 12;
            int hx = (ClientSize.Width - hw) / 2, hy = 24;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(190, 45, 45, 45)))
                e.Graphics.FillRectangle(b, hx, hy, hw, hh);
            TextRenderer.DrawText(e.Graphics, hint, this.Font, new Rectangle(hx, hy, hw, hh),
                                  Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ---- 自测钩子：不真的动鼠标，直接走同一套鼠标消息处理 ----
        internal void TestMouseDown(Point p) { TestDispatch(delegate { OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0)); }); }
        internal void TestMouseMove(Point p) { TestDispatch(delegate { OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, p.X, p.Y, 0)); }); }
        internal void TestMouseUp(Point p) { TestDispatch(delegate { OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0)); }); }

        private void TestDispatch(MethodInvoker body)
        {
            _testInput = true;
            try { body(); }
            finally { _testInput = false; }
        }
    }

    internal sealed class ChatForm : Form
    {
        // 用户点 Ctrl+V 时，编辑框内部就是靠这条消息触发的 —— UI 自测也要用它
        private const int WM_PASTE = 0x0302;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // ---- 路径：全部以 exe 所在目录为基准 ----
        private readonly string _dir = Application.StartupPath;
        private string KeyPath { get { return Path.Combine(_dir, "apikey.txt"); } }
        private string CfgPath { get { return Path.Combine(_dir, "config.txt"); } }

        /// <summary>提示词文件：优先 提示词.txt，兼容 prompt.txt。</summary>
        private string PromptPath()
        {
            string zh = Path.Combine(_dir, "提示词.txt");
            if (File.Exists(zh)) return zh;
            string en = Path.Combine(_dir, "prompt.txt");
            if (File.Exists(en)) return en;
            return zh;
        }

        // ---- 运行状态 ----
        private Config _cfg = new Config();
        private string _apiKey = "";
        private string _cfgPromptRaw;          // config.txt 里写的 system_prompt（可能为 null）
        private string _promptSource = "";     // 当前提示词来自哪里
        private int _promptChars;              // 当前提示词多少字
        private string _lastGoodPrompt;        // 上一次成功读到的提示词文件内容（读失败时兜底）
        private string _promptReadWarning;     // 本次提示词文件读取失败的提醒（会用完即清）
        private int _lastSentHistory;          // 上一轮实际送出去的历史消息条数
        private readonly List<Msg> _history = new List<Msg>();
        private Bitmap _pendingImage;          // 待发送的图片（原图，仅用于预览）
        private string _pendingImageUrl;       // 待发送的图片（已压缩成 data URL）
        private string _pendingImageInfo = "";
        private DateTime _clipStamp = DateTime.MinValue;
        private volatile bool _busy;
        private DeepSeekClient _client;
        private readonly Stopwatch _watch = new Stopwatch();

        // ---- 控件 ----
        private RichTextBox _chat;
        private PasteAwareTextBox _input;
        private Button _sendBtn, _stopBtn, _clearBtn, _reloadBtn, _promptBtn, _shotBtn;
        private CheckBox _thinkChk;
        private Label _status;
        private Panel _imgPanel;
        private PictureBox _imgPreview;
        private Label _imgInfo;
        private Button _imgRemoveBtn;
        private readonly ToolTip _tips = new ToolTip();

        private ContextMenuStrip _menu;                       // 右键菜单：极简模式下的全部功能入口
        private readonly StringBuilder _fullLog = new StringBuilder();  // 完整对话（输出行只显示一行，全文存这里）
        private bool _firstChunkOfTurn;                       // 本轮第一个片段到达时替换掉“生成中”占位

        // ---- 字体缓存（避免每次追加文本都 new Font） ----
        private Font _fontNormal, _fontBold, _fontItalic, _fontBoldItalic;
        private float _scale = 1f;
        private const float BaseFontPt = 9.5f;

        // ---- 取真实 DPI 需要的 Win32 ----
        // 说明：Graphics.FromHwnd(IntPtr.Zero).DpiX 在无清单进程里会返回 96（GDI 虚拟化），
        //       所以这里多路探测取最大值，任何一条拿到真值就不会误判。
        [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr CreateDC(string driver, string device, string output, IntPtr initData);
        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int index);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        private const int LOGPIXELSX = 88;

        public ChatForm()
        {
            Text = "Chat";
            AutoScaleMode = AutoScaleMode.None;     // 排版由 DoLayout 全权负责，别让 WinForms 再插一脚
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;

            CreateHandle();                          // 先有句柄，才能问出这台机器的真实 DPI
            _scale = MeasureScale(this);
            BuildFonts();
            BuildUi();      // 建控件 + 读 apikey/config/提示词（界面模式在这一步就定下来）

            // 任务栏图标开关：必须在注册全局热键之前设好——改这个属性会重建窗口句柄，
            // 先注册热键再改的话，热键会挂在被销毁的旧句柄上，按了没反应。
            ShowInTaskbar = _cfg.ShowTaskbarIcon;
            ApplyWindowOpacity();   // 同理：改透明度也会重建句柄，必须排在注册热键之前
            RegisterHotkeys();
            StartShowWatcher();
            ApplyUiMode();
            SetupTray();
            ApplyInitialSize();
            DoLayout();
        }

        /// <summary>自测用：强制界面模式（null = 按 config.txt）。</summary>
        internal static string UiModeOverride;

        // ------------------------------------------------------------------
        //  7.1 界面搭建
        // ------------------------------------------------------------------
        private void BuildFonts()
        {
            string[] prefer = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun" };
            string family = null;
            InstalledFontCollection fonts = new InstalledFontCollection();
            foreach (string want in prefer)
            {
                foreach (FontFamily f in fonts.Families)
                {
                    if (string.Equals(f.Name, want, StringComparison.OrdinalIgnoreCase)) { family = want; break; }
                }
                if (family != null) break;
            }

            // 注意：字号用“点”，它本身就是 DPI 无关的 —— 144dpi 下系统会自动放大 1.5 倍，
            //       这里再乘 _scale 就会变成 2.25 倍，字会撑破排版。
            float size = BaseFontPt;
            if (family == null)
            {
                _fontNormal = new Font(FontFamily.GenericSansSerif, size);
            }
            else
            {
                _fontNormal = new Font(family, size);
            }
            _fontBold = new Font(_fontNormal, FontStyle.Bold);
            _fontItalic = new Font(_fontNormal, FontStyle.Italic);
            _fontBoldItalic = new Font(_fontNormal, FontStyle.Bold | FontStyle.Italic);
            Font = _fontNormal;
        }

        private int px(int v) { return (int)Math.Round(v * _scale); }

        // ------------------------------------------------------------------
        //  7.1b 界面模式：compact（极简两行）/ full（完整界面）
        // ------------------------------------------------------------------

        /// <summary>
        /// 极简模式：整条窗口只有一行输出 + 一行输入；挂了图片时输入行右侧出现“移除图片”。
        /// 发送/停止/清空/思考开关/重新读取/编辑提示词 全部收进右键菜单，功能一个不少。
        /// </summary>
        private void ApplyUiMode()
        {
            bool compact = _cfg.CompactUI;

            _sendBtn.Visible = !compact;
            _stopBtn.Visible = !compact;
            _clearBtn.Visible = !compact;
            _reloadBtn.Visible = !compact;
            _promptBtn.Visible = !compact;
            _status.Visible = !compact;
            _thinkChk.Visible = !compact;

            // 输入框：极简模式退化成真正的单行输入（Enter 发送、Shift 无用）
            _input.Multiline = !compact;
            _input.ScrollBars = compact ? ScrollBars.None : ScrollBars.Vertical;
            _input.AcceptsReturn = !compact;

            // 输出框：极简模式正好一行高，不显示滚动条，靠自动滚动显示最新内容
            // 注意：ScrollBars=None 时 RichEdit 的 ScrollToCaret() 是空操作（实测最后一行滚不进来），
            //       所以即使只有一行高也必须保留纵向滚动条，否则最新内容会被滚到可视区外面。
            _chat.ScrollBars = RichTextBoxScrollBars.Vertical;
            _chat.BorderStyle = compact ? BorderStyle.None : BorderStyle.FixedSingle;
            _chat.TabStop = !compact;      // 极简模式下光标不要停在输出行上（看着像输入框）
            BackColor = compact ? Color.White : SystemColors.Control;

            // 图片区：极简模式不再显示缩略图和尺寸信息，只留“移除图片”
            _imgPreview.Visible = !compact;
            _imgInfo.Visible = !compact;
            _imgRemoveBtn.Text = compact ? "移除图片" : "移除图片 (×)";
            _imgPanel.BackColor = compact ? Color.White : Color.FromArgb(245, 248, 252);

            // 截图按钮两种模式都留着（极简模式也是刚需：题目截图）
            _shotBtn.Text = compact ? "截图"
                                    : "截图" + (_hotRegistered ? " (" + _hotName + ")" : "");

            BuildContextMenu();
        }

        /// <summary>初始尺寸：极简模式的高度是算出来的两行高度（锁死高度，只能横向拉宽）。</summary>
        private void ApplyInitialSize()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int wantW = px(_cfg.WindowWidth > 0 ? _cfg.WindowWidth : (_cfg.CompactUI ? 560 : 620));

            if (_cfg.CompactUI)
            {
                int cw = Math.Min(wantW, Math.Max(px(280), wa.Width - px(40)));
                int ch = CompactTotalHeight();
                ClientSize = new Size(cw, ch);

                // 注意：MinimumSize / MaximumSize 用的是“外框尺寸”，
                // 所以要拿当前 Size 反推边框高度，否则会被系统挤掉几像素。
                int frameH = Height - ClientSize.Height;
                MinimumSize = new Size(Math.Min(px(320), cw), ch + frameH);
                MaximumSize = new Size(Math.Max(px(600), wa.Width), ch + frameH);
            }
            else
            {
                int cw = Math.Min(wantW, Math.Max(px(420), wa.Width - px(40)));
                int ch = Math.Min(px(_cfg.WindowHeight > 0 ? _cfg.WindowHeight : 460),
                                  Math.Max(px(340), wa.Height - px(80)));
                ClientSize = new Size(cw, ch);
                MinimumSize = new Size(Math.Min(px(520), cw), Math.Min(px(400), ch));
                MaximumSize = new Size(0, 0);       // 0,0 = 不限制
            }
        }

        /// <summary>极简模式的两行尺寸：输出行高度、输入行高度（都是物理像素）。</summary>
        private void CompactMetrics(out int margin, out int gap, out int outH, out int inH)
        {
            margin = px(8);
            gap = px(6);
            outH = _fontNormal.Height + px(6);
            int natural = _input.PreferredHeight;      // 单行 TextBox 的自然高度
            inH = (natural > 0 && natural <= px(60)) ? natural : outH;
        }

        private int CompactTotalHeight()
        {
            int m, gap, outH, inH;
            CompactMetrics(out m, out gap, out outH, out inH);
            return m + outH + gap + inH + m;
        }

        /// <summary>多路探测真实 DPI，转成缩放系数（96dpi = 1.0）。</summary>
        // ------------------------------------------------------------------
        //  7.1c 截图：全局热键 + 框选
        // ------------------------------------------------------------------

        /// <summary>注册全局热键（在别的程序里按也能触发）：截图 + 显示/隐藏窗口。</summary>
        /// <summary>
        /// 应用窗口透明度（config.txt 的 window_opacity，百分比）。
        /// 提示：窗口设为半透明后就不再是不透明层，系统会关掉 ClearType，
        /// 字会比 100% 时略微发虚一点 —— 这是半透明窗口的固有代价。
        /// </summary>
        private void ApplyWindowOpacity()
        {
            try
            {
                int percent = OpacityOverride.HasValue ? OpacityOverride.Value : _cfg.WindowOpacity;
                Opacity = Util.ClampOpacity(percent) / 100.0;
            }
            catch { }
        }

        private void RegisterHotkeys()
        {
            if (SkipHotkeyRegistration)
            {
                _hotRegistered = false;
                _winHotRegistered = false;
                _hotName = "Ctrl+Alt+Q";      // 仅用于界面文字显示
                _winHotName = "Ctrl+Alt+C";
                return;
            }
            _hotRegistered = RegisterOneHotkey(HOTKEY_ID, _cfg.ScreenshotHotkey, "Ctrl+Alt+Q", out _hotName);
            _winHotRegistered = RegisterOneHotkey(HOTKEY_ID_WINDOW, _cfg.WindowHotkey, "Ctrl+Alt+C", out _winHotName);
        }

        /// <summary>
        /// 注册单个热键：配置写法不认识或被别的程序占用时，退回内置默认组合，
        /// 尽量保证“按了有反应”，而不是静默失效。
        /// </summary>
        private bool RegisterOneHotkey(int id, string configured, string fallback, out string name)
        {
            uint mods, vk;
            if (!Util.ParseHotkey(configured, out mods, out vk, out name))
                if (!Util.ParseHotkey(fallback, out mods, out vk, out name)) { name = ""; return false; }

            bool ok = false;
            try { ok = RegisterHotKey(Handle, id, mods, vk); }
            catch { ok = false; }

            if (!ok)
            {
                uint m2, v2; string n2;
                if (Util.ParseHotkey(fallback, out m2, out v2, out n2) && !(m2 == mods && v2 == vk))
                    try { ok = RegisterHotKey(Handle, id, m2, v2); name = n2; }
                    catch { }
            }
            if (!ok) name = "";
            return ok;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                StartCapture();
                return;
            }
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID_WINDOW)
            {
                // 正在框选时不要切窗口：否则会把窗口显示在“截图快照”下面，看起来像画面重叠/卡住
                if (_capturing) return;
                ToggleWindowVisible();
                return;
            }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------------
        //  7.1d 托盘图标：任务栏不显示图标时，用它把窗口找回来
        // ------------------------------------------------------------------

        /// <summary>
        /// 监听“把窗口叫出来”的信号：用户又双击了一次 exe 时（第二个实例会因为
        /// 单实例锁直接退出）由它触发，把已有窗口显示出来、光标放回输入框，
        /// 而不是再开一个窗口、再抢一次热键。
        /// </summary>
        private void StartShowWatcher()
        {
            try
            {
                bool createdNew;
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out createdNew);
                _showWatcher = new Thread(delegate ()
                {
                    while (true)
                    {
                        try { _showEvent.WaitOne(); }
                        catch { return; }                 // 事件被释放 = 程序在退出
                        if (IsDisposed) return;
                        ShowWakeCount++;
                        UiPost(delegate { ShowFromTray(); });
                    }
                });
                _showWatcher.IsBackground = true;
                _showWatcher.Start();
            }
            catch { }
        }

        /// <summary>把窗口显示出来并激活（已在最前面时也重新聚焦到输入框）。</summary>
        /// <summary>
        /// 构建时间戳（= exe 文件的修改时间）。
        /// 用途：排查问题时一眼看出“你现在跑的是哪一版”。
        /// 我换过多次 exe，而正在运行的进程不会自动更新，很容易在旧版上复现已修好的问题。
        /// </summary>
        internal static string BuildStamp()
        {
            try { return File.GetLastWriteTime(Application.ExecutablePath).ToString("yyyy-MM-dd HH:mm"); }
            catch { return "未知"; }
        }

        private void ShowFromTray()
        {
            try
            {
                if (!Visible) Show();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
                _input.Focus();
                // 隐藏期间可能改过布局（例如截图刚挂上图片行），显示回来必须按当前尺寸重排一次，
                // 否则会出现“图片行叠在输入框上”这种错位（用户截图里那种残块就是这个）。
                DoLayout();
                // 从隐藏状态回来后强制整窗重绘：否则偶尔会残留上一次的画面（用户截图里那种残块）
                Invalidate(true);
                Update();
            }
            catch (Exception ex) { LastShowError = ex.GetType().Name + ": " + ex.Message; }
        }

        /// <summary>
        /// 任务栏不显示图标时创建托盘图标（任务栏有图标就不重复占托盘）。
        /// 为什么必须有它：没有任务栏按钮时，窗口一旦被最小化或被其它窗口盖住，
        /// 用户就没有地方点回来了 —— 托盘菜单是唯一的退路，不能省。
        /// </summary>
        private void SetupTray()
        {
            if (_cfg.ShowTaskbarIcon || _tray != null) return;

            _trayMenu = new ContextMenuStrip();
            _trayMenu.Font = _fontNormal;
            _trayMenu.Items.Add(MenuItem("显示 / 隐藏窗口" + (_winHotRegistered ? "（" + _winHotName + "）" : ""),
                                         delegate { ToggleWindowVisible(); }));
            _trayMenu.Items.Add(MenuItem("截图框选" + (_hotRegistered ? "（" + _hotName + "）" : ""),
                                         delegate { StartCapture(); }));
            _trayMenu.Items.Add(MenuItem("查看完整对话", delegate
            {
                if (!Visible) ToggleWindowVisible();
                ShowFullLog();
            }));
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(MenuItem("退出 Chat", delegate { ExitApp(); }));

            _tray = new NotifyIcon();
            _tray.Icon = MakeTrayIcon();
            _tray.Text = "Chat　构建 " + BuildStamp() + "\r\n双击显示 / 隐藏窗口";
            _tray.ContextMenuStrip = _trayMenu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ToggleWindowVisible(); };
        }

        private void ToggleWindowVisible()
        {
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                Hide();
            }
            else
            {
                ShowFromTray();
            }
        }

        /// <summary>画一个 32×32 的托盘图标（蓝底对话气泡 + 白色 C），不依赖外部 ico 文件。</summary>
        internal static Icon MakeTrayIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 120, 215)))
                    {
                        g.FillRectangle(b, 2, 4, 28, 19);
                        g.FillPolygon(b, new Point[] {
                            new Point(8, 22), new Point(17, 22), new Point(8, 30) });
                    }
                    using (Font f = new Font("Arial", 13f, FontStyle.Bold))
                    using (SolidBrush w = new SolidBrush(Color.White))
                        g.DrawString("C", f, w, 9, 5);
                }

                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();       // 克隆出来，句柄释放后仍可用
                }
                finally { DestroyIcon(h); }             // 不销毁会泄漏 GDI 句柄
            }
        }

        private bool _capturing;

        /// <summary>自测用：注入一张假快照，避免自测真的全屏抓屏、遮住用户屏幕。</summary>
        internal static Bitmap CaptureOverrideForTest;

        /// <summary>
        /// 开始截图：把自己变透明 → 抓整屏快照 → 弹出选择层框选 → 裁剪 → 挂到对话里。
        /// </summary>
        private void StartCapture()
        {
            if (_capturing) return;
            _capturing = true;

            Bitmap shot = null;
            CaptureOverlay overlay = null;
            try
            {
                Rectangle vs;
                if (CaptureOverrideForTest != null)
                {
                    // 自测路径：用注入的假快照，不上屏遮屏
                    shot = new Bitmap(CaptureOverrideForTest);
                    vs = new Rectangle(0, 0, shot.Width, shot.Height);
                }
                else
                {
                    // 抓屏前把自己藏掉，避免把自己截进去。
                    // 注意：早先用的是 Opacity=0，但改透明度会让系统重建窗口句柄，
                    // 在“窗口本来就藏着”的场景下再显示出来会出现残留画面（用户截图里那种白块/残边框）。
                    // 所以：本来就藏着就什么都不做；本来可见就临时 Hide，抓完再 Show。
                    bool wasVisible = Visible && WindowState != FormWindowState.Minimized;
                    if (wasVisible)
                    {
                        Hide();
                        Application.DoEvents();
                        Thread.Sleep(120);
                        Application.DoEvents();
                    }
                    try
                    {
                        vs = SystemInformation.VirtualScreen;
                        shot = new Bitmap(vs.Width, vs.Height);
                        using (Graphics g = Graphics.FromImage(shot))
                            g.CopyFromScreen(vs.Left, vs.Top, 0, 0, new Size(vs.Width, vs.Height), CopyPixelOperation.SourceCopy);
                    }
                    finally
                    {
                        if (wasVisible) { Show(); Application.DoEvents(); }
                    }
                }

                overlay = new CaptureOverlay(shot, new Point(vs.Left, vs.Top));
                if (CaptureOverrideForTest != null) overlay.TestMode = true;   // 自测注入时忽略真实鼠标
                overlay.ShowDialog(this);
                Application.DoEvents();

                if (overlay.Confirmed)
                {
                    Rectangle r = overlay.Selection;
                    r.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
                    if (r.Width >= 4 && r.Height >= 4)
                    {
                        using (Bitmap crop = shot.Clone(r, shot.PixelFormat))
                            SetPendingImage(crop, "截图 " + r.Width + "×" + r.Height);
                        // 窗口本来就是藏着的（用托盘或热键藏起来的）→ 截图完把它显出来，
                        // 否则你看不到“图已经挂进对话”，还得自己再打开一次。
                        if (!Visible || WindowState == FormWindowState.Minimized) ShowFromTray();
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus("截图失败：" + ex.Message, true);
            }
            finally
            {
                try { if (overlay != null) overlay.Dispose(); } catch { }
                if (shot != null) shot.Dispose();
                _capturing = false;
                try { _input.Focus(); } catch { }           // 截图完直接把光标放回输入框
            }
        }

        private static float MeasureScale(Form f)
        {
            int dpi = MeasureDpiRaw(f);
            float s = dpi / 96f;
            if (s < 1f) s = 1f;
            if (s > 3f) s = 3f;
            return s;
        }

        private static int MeasureDpiRaw(Form f)
        {
            int best = 0;

            // ① 本窗口自己的 DC：DPI 感知进程里最准的一条
            if (f != null && f.IsHandleCreated)
            {
                IntPtr hdc = IntPtr.Zero;
                try
                {
                    hdc = GetDC(f.Handle);
                    if (hdc != IntPtr.Zero) best = Math.Max(best, GetDeviceCaps(hdc, LOGPIXELSX));
                }
                catch { }
                finally { if (hdc != IntPtr.Zero) ReleaseDC(f.Handle, hdc); }

                try { using (Graphics g = f.CreateGraphics()) best = Math.Max(best, (int)Math.Round(g.DpiX)); }
                catch { }
            }

            // ② 桌面 DC
            IntPtr desk = IntPtr.Zero;
            try
            {
                desk = GetDC(IntPtr.Zero);
                if (desk != IntPtr.Zero) best = Math.Max(best, GetDeviceCaps(desk, LOGPIXELSX));
            }
            catch { }
            finally { if (desk != IntPtr.Zero) ReleaseDC(IntPtr.Zero, desk); }

            // ③ 显示设备 DC
            IntPtr disp = IntPtr.Zero;
            try
            {
                disp = CreateDC("DISPLAY", null, null, IntPtr.Zero);
                if (disp != IntPtr.Zero) best = Math.Max(best, GetDeviceCaps(disp, LOGPIXELSX));
            }
            catch { }
            finally { if (disp != IntPtr.Zero) DeleteDC(disp); }

            if (best <= 0) best = 96;
            return best;
        }

        /// <summary>DPI 诊断信息（自检日志用，出问题时一眼能看出缩放判断对不对）。</summary>
        internal static string DpiReport(Form f)
        {
            StringBuilder sb = new StringBuilder();
            int hwndDc = 0, graphicsDpi = 0, deskDc = 0, dispDc = 0;
            try
            {
                IntPtr hdc = GetDC(f.Handle);
                if (hdc != IntPtr.Zero) { hwndDc = GetDeviceCaps(hdc, LOGPIXELSX); ReleaseDC(f.Handle, hdc); }
                using (Graphics g = f.CreateGraphics()) graphicsDpi = (int)Math.Round(g.DpiX);
                IntPtr d = GetDC(IntPtr.Zero);
                if (d != IntPtr.Zero) { deskDc = GetDeviceCaps(d, LOGPIXELSX); ReleaseDC(IntPtr.Zero, d); }
                IntPtr c = CreateDC("DISPLAY", null, null, IntPtr.Zero);
                if (c != IntPtr.Zero) { dispDc = GetDeviceCaps(c, LOGPIXELSX); DeleteDC(c); }
            }
            catch { }
            sb.AppendFormat("窗口DC={0}  Graphics={1}  桌面DC={2}  显示DC={3}  →  采用缩放={4:F2}×",
                            hwndDc, graphicsDpi, deskDc, dispDc, ((ChatForm)f)._scale);
            return sb.ToString();
        }

        private void BuildUi()
        {
            _chat = new RichTextBox();
            _chat.ReadOnly = true;
            _chat.BackColor = Color.White;
            _chat.BorderStyle = BorderStyle.FixedSingle;
            _chat.ScrollBars = RichTextBoxScrollBars.Vertical;
            _chat.WordWrap = true;
            _chat.HideSelection = false;
            _chat.DetectUrls = true;
            _chat.Font = _fontNormal;
            _chat.TabStop = true;
            _chat.ShortcutsEnabled = true;
            Controls.Add(_chat);

            _input = new PasteAwareTextBox();
            _input.Multiline = true;
            _input.ScrollBars = ScrollBars.Vertical;
            _input.AcceptsReturn = true;
            _input.AcceptsTab = false;
            _input.BorderStyle = BorderStyle.FixedSingle;
            _input.Font = _fontNormal;
            _input.OnPasteImage = TryPasteClipboardImage;
            _input.KeyDown += InputKeyDown;
            Controls.Add(_input);

            _imgPanel = new Panel();
            _imgPanel.BorderStyle = BorderStyle.FixedSingle;
            _imgPanel.BackColor = Color.FromArgb(245, 248, 252);
            _imgPanel.Visible = false;

            _imgPreview = new PictureBox();
            _imgPreview.SizeMode = PictureBoxSizeMode.Zoom;
            _imgPreview.BackColor = Color.White;
            _imgPreview.BorderStyle = BorderStyle.FixedSingle;
            _imgPanel.Controls.Add(_imgPreview);

            _imgInfo = new Label();
            _imgInfo.AutoSize = false;
            _imgInfo.TextAlign = ContentAlignment.MiddleLeft;
            _imgInfo.ForeColor = Color.FromArgb(60, 60, 60);
            _imgPanel.Controls.Add(_imgInfo);

            _imgRemoveBtn = new Button();
            _imgRemoveBtn.Text = "移除图片 (×)";
            _imgRemoveBtn.Click += delegate { ClearPendingImage("已移除待发送的图片。"); };
            _imgPanel.Controls.Add(_imgRemoveBtn);
            Controls.Add(_imgPanel);

            _sendBtn = NewButton("发送 (Enter)", SendCurrentInput);
            _stopBtn = NewButton("停止", delegate { StopGenerating(); });
            _clearBtn = NewButton("清空对话", delegate { ClearConversation(); });
            _reloadBtn = NewButton("重新读取 Key/配置", delegate { LoadKeyAndConfig(true); });
            _promptBtn = NewButton("编辑提示词", delegate { EditPromptFile(); });
            _shotBtn = NewButton("截图", delegate { StartCapture(); });
            _stopBtn.Enabled = false;
            _tips.SetToolTip(_shotBtn, "框选屏幕区域截图，截完自动挂到对话里（全局快捷键，在别的程序里也能按）");

            _thinkChk = new CheckBox();
            _thinkChk.Text = ThinkLongText;    // 窗口窄时 DoLayout 会自动换成短标签
            _thinkChk.AutoSize = true;
            _thinkChk.Visible = true;
            Controls.Add(_thinkChk);
            _tips.SetToolTip(_thinkChk, "勾上 = 先输出思考过程再回答（慢，但难题更准）\r\n不勾 = 直接回答（快）");
            _tips.SetToolTip(_reloadBtn, "重新读取 apikey.txt、config.txt、提示词.txt");
            _tips.SetToolTip(_promptBtn, "用记事本打开提示词.txt（角色设定 / 背景说明），保存后下一条消息生效");

            _status = new Label();
            _status.AutoSize = false;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.AutoEllipsis = true;      // 文字太长时显示“…”，不要硬切在半个字上
            _status.ForeColor = Color.FromArgb(90, 90, 90);
            Controls.Add(_status);

            // 说明：输入框的灰色占位提示试过两条路——
            //   ① EM_SETCUEBANNER：需要程序带 comctl32 v6 清单，本程序是 csc 直接编译的
            //      （没有清单），实测不显示；
            //   ② 用一个 Label 盖在输入框上：能显示，但中文输入法组字时 TextChanged 不触发，
            //      标签会盖住候选词框。
            // 所以最后不用占位提示，改成：启动时输出行显示操作提示 + 输入框鼠标悬停提示。
            _tips.SetToolTip(_input, "Enter 发送 · Ctrl+V 贴图 · 右键更多功能");

            // 拖入图片文件也支持
            DragEnter += FormDragEnter;
            DragDrop += FormDragDrop;
            FormClosing += FormClosingHandler;
            FormClosed += delegate
            {
                try { if (_hotRegistered) { UnregisterHotKey(Handle, HOTKEY_ID); _hotRegistered = false; } }
                catch { }
                try { if (_winHotRegistered) { UnregisterHotKey(Handle, HOTKEY_ID_WINDOW); _winHotRegistered = false; } }
                catch { }
                try
                {
                    if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                    if (_trayMenu != null) { _trayMenu.Dispose(); _trayMenu = null; }
                }
                catch { }
                try { if (_showEvent != null) { _showEvent.Close(); _showEvent = null; } }
                catch { }
                if (_pendingImage != null) { _pendingImage.Dispose(); _pendingImage = null; }
                _fontNormal.Dispose(); _fontBold.Dispose(); _fontItalic.Dispose(); _fontBoldItalic.Dispose();
            };
            Resize += delegate { DoLayout(); };
            Shown += delegate { _input.Focus(); };   // 启动就把光标放进输入框，直接能打字

            LoadKeyAndConfig(false);
            ShowWelcome();
        }

        private Button NewButton(string text, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.Font = _fontNormal;
            b.Click += onClick;
            Controls.Add(b);
            return b;
        }

        /// <summary>手工排版：不依赖 Dock 的 z 序规则，行为在任何 .NET 版本上都可预测。</summary>
        private void DoLayout()
        {
            if (_cfg.CompactUI) CompactLayout();
            else FullLayout();
        }

        /// <summary>
        /// 极简模式排版：上面一行输出、下面一行输入；
        /// 挂了图片时，输入行右侧多出一个“移除图片”按钮（不显示缩略图、不显示尺寸信息）。
        /// </summary>
        private void CompactLayout()
        {
            int W = ClientSize.Width;
            int m, gap, outH, inH;
            CompactMetrics(out m, out gap, out outH, out inH);
            int availW = Math.Max(px(120), W - 2 * m);

            _chat.SetBounds(m, m, availW, outH);

            int iy = m + outH + gap;
            // 从右往左排：输入框 ← 截图 ←（挂了图片时才有的）移除图片
            int right = W - m;
            if (_imgPanel.Visible)
            {
                int textW = TextRenderer.MeasureText(_imgRemoveBtn.Text, _imgRemoveBtn.Font).Width;
                int removeW = Math.Min(availW / 3, textW + px(20));
                _imgPanel.SetBounds(right - removeW, iy, removeW, inH);
                _imgRemoveBtn.SetBounds(0, 0, removeW, inH);
                right -= removeW + gap;
            }
            int shotW = Math.Min(availW / 3,
                                 TextRenderer.MeasureText(_shotBtn.Text, _shotBtn.Font).Width + px(20));
            _shotBtn.SetBounds(right - shotW, iy, shotW, inH);
            right -= shotW + gap;

            _input.SetBounds(m, iy, Math.Max(px(60), right - m), inH);

            // 兜底：按钮必须永远压在输入框上面。
            // 输入框是先创建的控件，在 WinForms 里"先创建的在前"，一旦位置算错，
            // 输入框会盖住按钮左边的一两个字（用户截图里就出现过“移除图片”变成“除图片”）。
            if (_imgPanel.Visible) _imgPanel.BringToFront();
            _shotBtn.BringToFront();
        }

        /// <summary>完整界面排版（老版本那套：对话区 + 按钮行 + 状态行）。</summary>
        private void FullLayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            int m = px(10), gap = px(6);
            int statusH = px(20), btnH = px(30), inputH = px(84);
            int imgH = _imgPanel.Visible ? px(96) : 0;
            int availW = Math.Max(px(200), W - 2 * m);

            // ---- ① 按钮排布：先按文字量宽度；装不下就收紧内边距；再装不下才换行 ----
            Button[] btns = new Button[] { _sendBtn, _stopBtn, _clearBtn, _shotBtn, _reloadBtn, _promptBtn };
            int n = btns.Length;
            int[] textW = new int[n];
            int[] btnW = new int[n];
            int[] rowOf = new int[n];
            int textSum = 0;
            for (int i = 0; i < n; i++)
            {
                textW[i] = TextRenderer.MeasureText(btns[i].Text, btns[i].Font).Width;
                textSum += textW[i];
                btnW[i] = textW[i] + px(26);
            }

            int rows = 1;
            int pad = px(26);
            int total = textSum + n * pad + (n - 1) * gap;
            if (total > availW)
            {
                // 先压缩左右留白，尽量保持一行（按钮多、窗口小的时候更整齐）
                int room = availW - textSum - (n - 1) * gap;
                if (room >= n * px(8))
                {
                    pad = room / n;
                    for (int i = 0; i < n; i++) btnW[i] = textW[i] + pad;
                }
                else
                {
                    // 实在放不下：按行装箱（每行都不超过可用宽度）
                    pad = px(26);
                    for (int i = 0; i < n; i++) btnW[i] = Math.Min(availW, textW[i] + pad);
                    int used = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int need = (used == 0) ? btnW[i] : used + gap + btnW[i];
                        if (used > 0 && need > availW) { rows++; used = btnW[i]; }
                        else used = need;
                        rowOf[i] = rows - 1;
                    }
                }
            }
            int btnAreaH = rows * btnH + (rows - 1) * gap;

            // ---- ② 自下而上排 ----
            int bottom = statusH + gap + btnAreaH + gap + inputH + (imgH > 0 ? gap + imgH : 0);
            _chat.SetBounds(m, m, Math.Max(px(100), W - 2 * m), Math.Max(px(60), H - bottom - 2 * m));

            // 状态行：左下状态文字 + 右下“深度思考”。窗口窄时勾选框文字自动缩短，
            // 宁可少几个字，也不能让状态栏被挤没。
            _thinkChk.Text = ThinkLongText;
            Size think = _thinkChk.GetPreferredSize(Size.Empty);
            if (availW - think.Width - px(10) < px(200))
            {
                _thinkChk.Text = ThinkShortText;
                think = _thinkChk.GetPreferredSize(Size.Empty);
            }
            int thinkTop = H - m - statusH + (statusH - think.Height) / 2;
            _thinkChk.SetBounds(W - m - think.Width, thinkTop, think.Width, think.Height);
            _status.SetBounds(m, H - m - statusH,
                              Math.Max(px(60), availW - think.Width - px(10)), statusH);

            // 按钮行（行号已算好，左对齐逐行摆）
            int rowTop = H - m - statusH - gap - btnAreaH;
            for (int i = 0; i < n; i++)
            {
                int x = m;
                for (int j = 0; j < i; j++)
                    if (rowOf[j] == rowOf[i]) x += btnW[j] + gap;
                btns[i].SetBounds(x, rowTop + rowOf[i] * (btnH + gap), btnW[i], btnH);
            }

            // 输入框
            int iy = rowTop - gap - inputH;
            _input.SetBounds(m, iy, Math.Max(px(80), W - 2 * m), inputH);

            // 图片预览区（只在挂了图时出现）
            if (imgH > 0)
            {
                int py = iy - gap - imgH;
                _imgPanel.SetBounds(m, py, Math.Max(px(80), W - 2 * m), imgH);
                int side = imgH - px(10);
                _imgPreview.SetBounds(px(5), px(5), side, side);
                int removeW = Math.Max(px(46), Math.Min(px(110), _imgPanel.Width - side - px(30)));
                _imgRemoveBtn.SetBounds(_imgPanel.Width - removeW - px(6),
                                        px(5) + (side - btnH) / 2, removeW, btnH);
                _imgInfo.SetBounds(px(5) + side + px(10), px(5),
                                   Math.Max(px(40), _imgPanel.Width - side - removeW - px(26)), side);
            }
        }

        private const string ThinkLongText = "深度思考（慢，但难题更准）";
        private const string ThinkShortText = "深度思考";

        // ------------------------------------------------------------------
        //  7.2 配置与 Key
        // ------------------------------------------------------------------
        private void LoadKeyAndConfig(bool report)
        {
            _cfg = new Config();
            Dictionary<string, string> map = null;
            string cfgText = Util.ReadTextFile(CfgPath);
            if (cfgText != null)
            {
                try
                {
                    map = Util.ParseConfig(cfgText);
                    Util.ApplyConfigMap(_cfg, map);
                }
                catch { }
            }

            // config.txt 里写没写 system_prompt 要区分开：没写 ≠ 写了空
            _cfgPromptRaw = (map != null && map.ContainsKey("system_prompt")) ? map["system_prompt"] : null;
            ApplyPromptFile();
            // 自测用：强制指定界面模式。必须在这里生效——欢迎文字是按模式写的，
            // 放到构造函数后面再覆盖就会写出和当前模式不符的提示。
            if (UiModeOverride != null) _cfg.Ui = UiModeOverride;
            if (TaskbarOverride.HasValue) _cfg.ShowTaskbarIcon = TaskbarOverride.Value;

            _apiKey = Util.ParseApiKey(Util.ReadTextFile(KeyPath));
            if (string.IsNullOrEmpty(_apiKey)) _apiKey = Util.Clean(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"));

            _thinkChk.Checked = _cfg.Thinking;

            if (string.IsNullOrEmpty(_apiKey))
            {
                SetStatus("未读取到 API Key：" + KeyPath + "（把 sk- 开头的 Key 填进去即可）", true);
                if (report)
                    MessageBox.Show(this,
                        "没有读到 API Key。\r\n\r\n请把 Key 写进这个文件：\r\n" + KeyPath +
                        "\r\n\r\n文件里只写一行 sk-...  即可（也支持 apikey=sk-...）。",
                        "缺少 API Key", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                SetStatus("已读取 Key：" + Util.MaskKey(_apiKey) + "　|　模型：" + _cfg.Model +
                          "　|　思考：" + (_cfg.Thinking ? "开" : "关") +
                          "　|　提示词：" + _promptSource + "(" + _promptChars + "字)", false);
                if (report)
                    MessageBox.Show(this,
                        "已重新读取：\r\n\r\n" + KeyPath + "\r\n" + CfgPath + "\r\n" + PromptPath() + "\r\n\r\n" +
                        "当前提示词来源：" + _promptSource + "（" + _promptChars + " 字）",
                        "读取完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>
        /// 重新从 提示词.txt 取一段系统提示词。每次发送前都会调一次，
        /// 所以用记事本改完保存，下一条消息就生效，不用重启程序。
        /// 优先级：提示词.txt &gt; config.txt 的 system_prompt &gt; 内置默认。
        /// </summary>
        private void ApplyPromptFile()
        {
            string path = PromptPath();
            string fileText = null;
            bool readFailed = false;

            if (File.Exists(path))
            {
                fileText = Util.ReadTextFile(path);
                if (fileText == null)
                {
                    // 文件在、但这次读不出来（正被记事本/网盘占用等）。
                    // 这里绝不能悄悄退回 config.txt 里那句通用提示词——那正是
                    // “聊着聊着它忘了我的设定”的来源。改成沿用上次成功读到的内容。
                    readFailed = true;
                    fileText = _lastGoodPrompt;
                }
                else
                {
                    _lastGoodPrompt = fileText;
                }
            }
            else
            {
                _lastGoodPrompt = null;   // 文件确实被删了，那就按 config.txt / 内置默认走
            }

            string src;
            string effective = Util.ResolvePrompt(_cfgPromptRaw, fileText, Config.DefaultSystemPrompt, out src);
            _cfg.SystemPrompt = effective;
            _promptSource = readFailed ? src + "（沿用上次）" : src;
            _promptChars = effective == null ? 0 : effective.Length;

            if (readFailed)
            {
                _promptReadWarning = "提示词文件本次读取失败，已沿用上次内容";
                _fullLog.Append("\r\n[提示] ").Append(_promptReadWarning)
                         .Append("（").Append(Path.GetFileName(path)).Append("）\r\n");
            }
            else
            {
                _promptReadWarning = null;
            }
        }

        /// <summary>用记事本打开提示词文件；文件不存在就按模板新建一个。</summary>
        private void EditPromptFile()
        {
            try
            {
                string p = PromptPath();
                if (!File.Exists(p))
                {
                    File.WriteAllText(p, PromptTemplate, new UTF8Encoding(true));   // 带 BOM，老记事本也不会乱码
                    SetStatus("已新建 " + Path.GetFileName(p) + "，写好后保存即可。", false);
                }
                Process.Start("notepad.exe", "\"" + p + "\"");
                SetStatus("已用记事本打开 " + Path.GetFileName(p) + "：改完保存，下一条消息自动生效。", false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打不开记事本：\r\n" + ex.Message + "\r\n\r\n你可以手动编辑：\r\n" + PromptPath(),
                                "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>提示词文件缺失时用的模板。</summary>
        private const string PromptTemplate =
            "你是我的中文助手。回答先给结论，再给理由或步骤，不要绕弯子。\r\n" +
            "涉及数值请带上单位，涉及代码请给能直接用的完整写法。\r\n" +
            "信息不足时先问我一个问题，不要自己编。";

        private void ShowWelcome()
        {
            if (_cfg.CompactUI)
            {
                // 极简模式只有一行，只在“Key 正常”时显示一句操作提示；
                // 缺 Key 之类的问题由 SetStatus 直接写在输出行上，不能被提示覆盖。
                if (!string.IsNullOrEmpty(_apiKey)) SetOutputLine(CompactHint, Color.FromArgb(150, 150, 150));
                return;
            }

            AppendRun("Chat\r\n", Color.FromArgb(0, 80, 160), true, false);
            AppendRun("· 下面输入问题：Enter 发送，Shift+Enter 换行；输入框里 Ctrl+V 直接贴图。\r\n", Color.FromArgb(80, 80, 80), false, false);
            AppendRun("· Key 在 apikey.txt；提示词/背景说明在 " + Path.GetFileName(PromptPath()) +
                      "（当前来源：" + _promptSource + "，" + _promptChars + " 字，改完即生效）。\r\n",
                      Color.FromArgb(80, 80, 80), false, false);
            AppendRun("· 贴图要用 deepseek-flash 模型；“深度思考”不勾更快、勾上更准。\r\n",
                      Color.FromArgb(80, 80, 80), false, false);
        }

        private const string CompactHint = "Ctrl+V 贴图 · Enter 发送 · 叉号只是隐藏 · 右键更多";

        private void SetStatus(string text, bool warning)
        {
            _status.Text = text;
            _status.ForeColor = warning ? Color.FromArgb(190, 60, 0) : Color.FromArgb(90, 90, 90);

            // 极简模式没有状态栏：只有“必须让你知道”的告警才占用那一行输出
            if (_cfg.CompactUI && warning) SetOutputLine(text, Color.FromArgb(190, 60, 0));
        }

        // ------------------------------------------------------------------
        //  7.3 图片粘贴 / 拖入
        // ------------------------------------------------------------------
        /// <summary>Ctrl+V：剪贴板有图就拦下来，返回 true 表示不再做文本粘贴。</summary>
        private bool TryPasteClipboardImage()
        {
            Image img = null;
            for (int attempt = 0; attempt < 2 && img == null; attempt++)
            {
                try
                {
                    if (Clipboard.ContainsImage()) img = Clipboard.GetImage();
                }
                catch (Exception)
                {
                    Thread.Sleep(60);   // 剪贴板被别的程序占用时等一下重试
                }
            }
            if (img == null) return false;      // 没有图片 → 走普通文本粘贴

            // 同一个图重复粘贴（Ctrl+V 连按）时忽略，避免重复处理大图
            if (DateTime.Now - _clipStamp < TimeSpan.FromMilliseconds(300)) { img.Dispose(); return true; }
            _clipStamp = DateTime.Now;

            SetPendingImage(img, "剪贴板图片");
            img.Dispose();
            return true;
        }

        private void FormDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0 && IsImageFile(files[0]))
                {
                    e.Effect = DragDropEffects.Copy;
                    return;
                }
            }
            e.Effect = DragDropEffects.None;
        }

        private void FormDragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            if (!IsImageFile(files[0])) return;
            try
            {
                using (Image img = Image.FromFile(files[0]))
                    SetPendingImage(img, Path.GetFileName(files[0]));
            }
            catch (Exception ex)
            {
                SetStatus("这个文件读不了（只支持 PNG/JPG/BMP/GIF）：" + ex.Message, true);
            }
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif";
        }

        /// <summary>把一张图收进“待发送”状态：压缩成 data URL + 显示缩略图。</summary>
        private void SetPendingImage(Image img, string name)
        {
            string info;
            string dataUrl = null;
            try { dataUrl = ImageCodec.ToDataUrl(img, out info); }
            catch (Exception ex) { SetStatus("图片处理失败：" + ex.Message, true); return; }
            if (dataUrl == null) { SetStatus("图片处理失败：编码不支持。", true); return; }

            ClearPendingImage(null);

            Bitmap preview = new Bitmap(img);
            _pendingImage = preview;
            _pendingImageUrl = dataUrl;
            _pendingImageInfo = name + "　" + info;
            _imgPreview.Image = preview;
            _imgInfo.Text = _pendingImageInfo + "\r\n发送时随问题一起上传，原图不落盘。";
            _imgPanel.Visible = true;
            DoLayout();
            SetStatus("图片已就绪：" + _pendingImageInfo + "　按 Enter 连同问题一起发送。", false);
        }

        private void ClearPendingImage(string statusText)
        {
            // 先让 PictureBox 放下引用，再释放位图，否则重绘时会访问已释放对象
            _imgPreview.Image = null;
            if (_pendingImage != null) { _pendingImage.Dispose(); _pendingImage = null; }
            _pendingImageUrl = null;
            _pendingImageInfo = "";
            _imgPanel.Visible = false;
            DoLayout();
            if (statusText != null) SetStatus(statusText, false);
        }

        // ------------------------------------------------------------------
        //  7.4 发送 / 停止 / 清空
        // ------------------------------------------------------------------
        private void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control && !e.Alt)
            {
                // 中文输入法正在拼字时按 Enter 是“选字”，不能当成发送
                if (IsImeComposing(_input.Handle)) return;
                e.SuppressKeyPress = true;      // 吃掉回车，避免输入框里多出空行
                SendCurrentInput(sender, e);
            }
        }

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);
        [DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        private static extern int ImmGetCompositionString(IntPtr hIMC, int dwIndex, IntPtr lpBuf, int dwBufLen);
        private const int GCS_COMPSTR = 0x0008;

        /// <summary>输入法是否正在组字（拼音还没上屏）。是的话回车归输入法处理。</summary>
        private static bool IsImeComposing(IntPtr hwnd)
        {
            IntPtr hImc = IntPtr.Zero;
            try
            {
                hImc = ImmGetContext(hwnd);
                if (hImc == IntPtr.Zero) return false;
                return ImmGetCompositionString(hImc, GCS_COMPSTR, IntPtr.Zero, 0) > 0;
            }
            catch { return false; }
            finally { if (hImc != IntPtr.Zero) { try { ImmReleaseContext(hwnd, hImc); } catch { } } }
        }

        private void SendCurrentInput(object sender, EventArgs e)
        {
            if (_busy) return;

            string text = _input.Text.Trim();
            if (text.Length == 0 && _pendingImageUrl == null)
            {
                SetStatus("先输入内容，或者在输入框里 Ctrl+V 粘一张图。", true);
                return;
            }
            if (string.IsNullOrEmpty(_apiKey))
            {
                LoadKeyAndConfig(false);
                if (string.IsNullOrEmpty(_apiKey))
                {
                    MessageBox.Show(this,
                        "还没读到 API Key，发不出去。\r\n\r\n请在记事本里打开这个文件：\r\n" + KeyPath +
                        "\r\n\r\n把 “apikey =” 后面填上你的 Key（sk- 开头）保存，\r\n" +
                        "再点界面上的“重新读取 Key/配置”即可。\r\n\r\n" +
                        "（注意确认文件名没被系统加成 apikey.txt.txt）",
                        "缺少 API Key", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            string imageUrl = _pendingImageUrl;
            string imageInfo = _pendingImageInfo;
            string shownText = text.Length > 0 ? text : "（看图，请描述这张图片）";

            // 本轮参数快照（克隆一份，改它不会污染全局配置）
            ApplyPromptFile();          // 每次发送前重读提示词文件：改完保存就直接生效
            Config snap = _cfg.Clone();
            snap.Thinking = _thinkChk.Checked;

            // 纯文本模型收到图片只会报错，先问用户要不要临时切换
            if (imageUrl != null && !snap.ModelSupportsImage)
            {
                DialogResult r = MessageBox.Show(this,
                    "当前模型 “" + snap.Model + "” 不支持图片。\r\n\r\n" +
                    "是否本轮改用支持图片的 deepseek-flash 发送？\r\n" +
                    "（选“否”会取消发送，你的文字和图片都还在）",
                    "模型不支持图片", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                snap.Model = "deepseek-flash";
                SetStatus("本轮已临时切换到 deepseek-flash。", false);
            }

            _history.Add(new Msg("user", shownText, imageUrl));
            TrimHistory();
            _lastSentHistory = Math.Max(0, _history.Count - 1);   // 不含本轮这一问

            if (_cfg.CompactUI)
            {
                // 极简模式：屏幕上只有一行，所以只显示“生成中”，
                // 完整对话（你问的、模型答的）记到 _fullLog 里，右键“查看完整对话”可看
                _fullLog.Append("\r\n【你】").Append(DateTime.Now.ToString("HH:mm:ss"))
                         .Append("　[本轮送出：系统提示词 ").Append(_cfg.SystemPrompt == null ? 0 : _cfg.SystemPrompt.Length)
                         .Append(" 字 + 历史 ").Append(_lastSentHistory).Append(" 条]");
                if (_promptReadWarning != null) _fullLog.Append("（").Append(_promptReadWarning).Append("）");
                _fullLog.Append("\r\n");
                if (imageUrl != null) _fullLog.Append("［附图片：").Append(imageInfo).Append("］\r\n");
                _fullLog.Append(shownText).Append("\r\n【DeepSeek】\r\n");
                SetOutputLine("…生成中", Color.FromArgb(150, 150, 150));
                _firstChunkOfTurn = true;
            }
            else
            {
                AppendRun("\r\n【你】" + DateTime.Now.ToString("HH:mm:ss") + "\r\n", Color.FromArgb(0, 80, 160), true, false);
                if (imageUrl != null)
                    AppendRun("［附图片：" + imageInfo + "］\r\n", Color.FromArgb(120, 120, 120), false, true);
                AppendRun(shownText + "\r\n", Color.FromArgb(20, 20, 20), false, false);
                AppendRun("【DeepSeek】" + (_thinkChk.Checked ? "（思考中…）" : "（生成中…）") + "\r\n",
                          Color.FromArgb(0, 120, 60), true, false);
            }

            _input.Clear();
            ClearPendingImage(null);

            _configSnapshot = snap;

            _busy = true;
            _sendBtn.Enabled = false;
            _stopBtn.Enabled = true;
            _watch.Reset();
            _watch.Start();
            SetStatus("正在请求 DeepSeek …", false);

            DeepSeekClient client = new DeepSeekClient(_configSnapshot, _apiKey);
            _client = client;
            Thread t = new Thread(delegate () { Worker(client); });
            t.IsBackground = true;
            t.Start();
        }

        private Config _configSnapshot = new Config();
        private bool _thinkHeaderShown;

        /// <summary>后台线程：调接口 + 流式回填界面。</summary>
        private void Worker(DeepSeekClient client)
        {
            string fullReasoning;
            try
            {
                _thinkHeaderShown = false;
                string answer = client.Chat(_history, OnDelta, out fullReasoning);

                if (answer != null && answer.Length > 0) _history.Add(new Msg("assistant", answer, null));
                TrimHistory();

                _watch.Stop();
                UiPost(delegate
                {
                    if (_cfg.CompactUI)
                    {
                        _fullLog.Append("\r\n");
                        CompactShowFinalLine();     // 收尾停在“最后一个有内容的行”，别留一行空白
                    }
                    else
                    {
                        AppendRun("\r\n", Color.Black, false, false);
                        SetStatus(string.Format("完成：用时 {0:F1} 秒，回答 {1} 字{2}",
                            _watch.Elapsed.TotalSeconds, answer == null ? 0 : answer.Length,
                            fullReasoning.Length > 0 ? "，思考 " + fullReasoning.Length + " 字" : "") +
                            string.Format("　|　本轮送出：系统提示词 {0} 字 + 历史 {1} 条{2}",
                                _configSnapshot.SystemPrompt == null ? 0 : _configSnapshot.SystemPrompt.Length,
                                _lastSentHistory,
                                _promptReadWarning != null ? "（" + _promptReadWarning + "）" : ""), false);
                    }
                    FinishTurn();
                });
            }
            catch (Exception ex)
            {
                _watch.Stop();
                string msg = ex.Message;
                UiPost(delegate
                {
                    if (_cfg.CompactUI)
                    {
                        // 一行窗口放不下长错误：整段进完整记录，屏幕上只显示第一行
                        _fullLog.Append("\r\n【出错】").Append(msg).Append("\r\n");
                        SetOutputLine(FirstLine(msg), Color.FromArgb(200, 30, 30));
                    }
                    else
                    {
                        AppendRun("\r\n【出错】" + msg + "\r\n", Color.FromArgb(200, 30, 30), false, false);
                        SetStatus("本轮失败：" + FirstLine(msg), true);
                    }
                    FinishTurn();
                });
            }
        }

        /// <summary>流式增量回调（后台线程）→ 转回 UI 线程追加文本。</summary>
        private void OnDelta(string reasoning, string content)
        {
            bool compact = _cfg.CompactUI;
            if (reasoning != null && reasoning.Length > 0)
            {
                if (!compact && !_thinkHeaderShown)
                {
                    _thinkHeaderShown = true;
                    UiPost(delegate { AppendRun("  ⌜思考过程⌟ ", Color.FromArgb(140, 140, 140), false, true); });
                }
                string r = reasoning;
                UiPost(delegate
                {
                    BeginOutputChunk();
                    AppendRun(r, Color.FromArgb(150, 150, 150), false, true);
                });
            }
            if (content != null && content.Length > 0)
            {
                if (!compact && _thinkHeaderShown)
                {
                    _thinkHeaderShown = false;
                    UiPost(delegate { AppendRun("\r\n  ⌞正式回答⌟\r\n", Color.FromArgb(140, 140, 140), false, true); });
                }
                string c = content;
                UiPost(delegate
                {
                    BeginOutputChunk();
                    AppendRun(c, Color.FromArgb(20, 20, 20), false, false);
                });
            }
        }

        private void FinishTurn()
        {
            _busy = false;
            _client = null;
            _sendBtn.Enabled = true;
            _stopBtn.Enabled = false;
            _input.Focus();
        }

        private void StopGenerating()
        {
            DeepSeekClient c = _client;
            if (c != null) c.Cancel();
            SetStatus("正在停止…", false);
        }

        private void ClearConversation()
        {
            if (_busy)
            {
                MessageBox.Show(this, "正在生成回答，先点“停止”再清空。", "提示",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _history.Clear();
            _fullLog.Length = 0;
            _chat.Clear();
            ShowWelcome();
            SetStatus("对话已清空（模型不再记得之前的内容）。", false);
        }

        /// <summary>历史裁剪：控条数 + 只保留最近若干条带图消息，防止 token 越滚越大。</summary>
        private void TrimHistory()
        {
            Util.TrimHistory(_history, _cfg.MaxHistoryMessages, _cfg.KeepImageTurns);
        }

        // ------------------------------------------------------------------
        //  7.4b 右键菜单：极简模式界面上只剩两行，全部功能从这里进
        // ------------------------------------------------------------------
        private void BuildContextMenu()
        {
            if (_menu != null)
            {
                ContextMenuStrip = null;
                if (_input != null) _input.ContextMenuStrip = null;
                if (_chat != null) _chat.ContextMenuStrip = null;
                _menu.Dispose();
                _menu = null;
            }

            _menu = new ContextMenuStrip();
            _menu.Font = _fontNormal;

            // 编辑类（输入框自己那个右键菜单被这个替换了，所以这里补齐）
            _menu.Items.Add(MenuItem("剪切（输入框）", delegate { try { _input.Cut(); } catch { } }));
            _menu.Items.Add(MenuItem("复制", delegate { MenuCopy(); }));
            _menu.Items.Add(MenuItem("粘贴（有图就收下图片）", delegate { MenuPaste(); }));
            _menu.Items.Add(MenuItem("全选输入框", delegate { _input.SelectAll(); _input.Focus(); }));
            _menu.Items.Add(new ToolStripSeparator());

            // 对话类
            _menu.Items.Add(MenuItem("发送 (Enter)", delegate { SendCurrentInput(this, EventArgs.Empty); }));
            _menu.Items.Add(MenuItem("停止生成", delegate { StopGenerating(); }));
            _menu.Items.Add(MenuItem("清空对话", delegate { ClearConversation(); }));
            _menu.Items.Add(MenuItem("查看完整对话", delegate { ShowFullLog(); }));
            _menu.Items.Add(MenuItem("截图框选" + (_hotRegistered ? "（" + _hotName + "）" : ""),
                                     delegate { StartCapture(); }));
            _menu.Items.Add(new ToolStripSeparator());

            // 参数类
            ToolStripMenuItem think = new ToolStripMenuItem("深度思考（慢，但难题更准）");
            think.CheckOnClick = true;
            think.Checked = _thinkChk.Checked;
            think.CheckedChanged += delegate { _thinkChk.Checked = think.Checked; };
            _menu.Items.Add(think);
            _menu.Items.Add(MenuItem("移除待发送的图片", delegate
            {
                if (_pendingImageUrl != null) ClearPendingImage("已移除待发送的图片。");
            }));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(MenuItem("重新读取 Key/配置", delegate { LoadKeyAndConfig(true); }));
            _menu.Items.Add(MenuItem("编辑提示词", delegate { EditPromptFile(); }));
            _menu.Items.Add(MenuItem("打开 config.txt", delegate { OpenInNotepad(CfgPath); }));
            _menu.Items.Add(new ToolStripSeparator());
            // 叉号改成“隐藏”之后，窗口菜单里必须留一个真正的退出入口
            _menu.Items.Add(MenuItem("退出 Chat（点叉号只是隐藏）", delegate { ExitApp(); }));

            ContextMenuStrip = _menu;
            _input.ContextMenuStrip = _menu;
            _chat.ContextMenuStrip = _menu;
        }

        private static ToolStripMenuItem MenuItem(string text, EventHandler onClick)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Click += onClick;
            return it;
        }

        /// <summary>复制：优先复制输出框里选中的内容，否则复制整行输出，再不行复制输入框选中。</summary>
        private void MenuCopy()
        {
            try
            {
                if (_chat.SelectionLength > 0) _chat.Copy();
                else if (_chat.TextLength > 0) Clipboard.SetText(_chat.Text);
                else if (_input.SelectionLength > 0) _input.Copy();
            }
            catch (Exception ex) { SetStatus("复制失败：" + ex.Message, true); }
        }

        /// <summary>粘贴：剪贴板里有图就当图片收下，否则按普通文本粘进输入框。</summary>
        private void MenuPaste()
        {
            try
            {
                if (TryPasteClipboardImage()) { _input.Focus(); return; }
                _input.Focus();
                _input.Paste();
            }
            catch (Exception ex) { SetStatus("粘贴失败：" + ex.Message, true); }
        }

        private void OpenInNotepad(string path)
        {
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, "", new UTF8Encoding(true));
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception ex) { SetStatus("打不开记事本：" + ex.Message, true); }
        }

        /// <summary>查看完整对话：极简模式屏幕上只有一行，全文从这里看，可一键复制。</summary>
        private void ShowFullLog()
        {
            Form dlg = new Form();
            dlg.Text = "Chat - 完整对话（构建 " + BuildStamp() + "）";
            dlg.Font = _fontNormal;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.MinimizeBox = false;
            dlg.ClientSize = new Size(px(620), px(420));
            dlg.MinimumSize = new Size(px(360), px(260));

            RichTextBox box = new RichTextBox();
            box.ReadOnly = true;
            box.WordWrap = true;
            box.BackColor = Color.White;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = _fontNormal;
            box.Dock = DockStyle.Fill;
            box.Text = _fullLog.Length > 0 ? _fullLog.ToString() : _chat.Text;

            Button copyBtn = new Button();
            copyBtn.Text = "复制全部";
            copyBtn.Font = _fontNormal;
            copyBtn.Dock = DockStyle.Fill;
            copyBtn.Margin = new Padding(px(6), px(6), px(6), px(6));
            copyBtn.Click += delegate { try { Clipboard.SetText(box.Text); } catch { } };

            Button closeBtn = new Button();
            closeBtn.Text = "关闭";
            closeBtn.Font = _fontNormal;
            closeBtn.Dock = DockStyle.Fill;
            closeBtn.Margin = new Padding(px(6), px(6), px(6), px(6));
            closeBtn.Click += delegate { dlg.Close(); };

            // 用 TableLayoutPanel 明确分行分列，避免 Dock 的 z 序规则在不同 .NET 版本上表现不一致
            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.ColumnCount = 3;
            bottom.RowCount = 1;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, px(130)));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, px(110)));
            bottom.Controls.Add(copyBtn, 0, 0);
            bottom.Controls.Add(closeBtn, 2, 0);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, px(44)));
            root.Controls.Add(box, 0, 0);
            root.Controls.Add(bottom, 0, 1);
            dlg.Controls.Add(root);

            dlg.ShowDialog(this);
            dlg.Dispose();
        }

        private void FormClosingHandler(object sender, FormClosingEventArgs e)
        {
            // 点窗口叉号（或 Alt+F4）默认只是隐藏到后台，不退出程序 ——
            // 退出请走右键菜单/托盘菜单里的“退出 Chat”。想改回直接退出：
            // config.txt 里 close_button = exit
            bool hideOnClose = !string.Equals(_cfg.CloseButton, "exit", StringComparison.OrdinalIgnoreCase);
            if (hideOnClose && !_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToBackground();
                return;
            }

            if (_busy)
            {
                DialogResult r = MessageBox.Show(this, "还在生成回答，确定要关闭吗？", "确认关闭",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                DeepSeekClient c = _client;
                if (c != null) c.Cancel();
            }
        }

        /// <summary>
        /// 隐藏到后台（叉号的行为）。第一次会明确告诉你“程序没退出、怎么叫回来、怎么真正退出”，
        /// 否则用户很容易以为已经关掉了，却发现进程还在。
        /// </summary>
        private void HideToBackground()
        {
            Hide();
            if (_closeHintShown) return;
            _closeHintShown = true;

            string back = _winHotRegistered ? ("按 " + _winHotName + " 唤回窗口；") : "";
            string quit = _tray != null ? "右键托盘图标 → 退出 Chat 可完全退出。"
                                        : "右键窗口 → 退出 Chat 可完全退出。";
            string msg = "Chat 没有退出，只是隐藏到后台了。" + back + quit;

            try
            {
                if (_tray != null && !SuppressNotifications)
                {
                    _tray.ShowBalloonTip(5000, "Chat 仍在后台运行", msg, ToolTipIcon.Info);
                    return;
                }
            }
            catch { }
            SetStatus(msg, false);
        }

        /// <summary>真正退出程序（菜单入口调它）。</summary>
        private void ExitApp()
        {
            _reallyExit = true;
            Close();
        }

        // ------------------------------------------------------------------
        //  7.5 文本追加与线程切换
        // ------------------------------------------------------------------
        private void AppendRun(string text, Color color, bool bold, bool italic)
        {
            if (text == null || text.Length == 0) return;
            _fullLog.Append(text);          // 完整对话单独留一份（极简模式只显示一行）

            Font f = _fontNormal;
            if (bold && italic) f = _fontBoldItalic;
            else if (bold) f = _fontBold;
            else if (italic) f = _fontItalic;

            _chat.SelectionStart = _chat.TextLength;
            _chat.SelectionLength = 0;
            _chat.SelectionColor = color;
            _chat.SelectionFont = f;
            _chat.AppendText(text);
            _chat.SelectionStart = _chat.TextLength;
            _chat.SelectionLength = 0;
            ScrollOutputToEnd();
        }

        private const int WM_VSCROLL = 0x0115;
        private const int SB_BOTTOM = 7;
        private const int WM_CLOSE = 0x0010;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_CLOSE = 0xF060;

        // ---- 全局截图热键 ----
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 0x5A17;          // 截图
        private const int HOTKEY_ID_WINDOW = 0x5A18;   // 显示/隐藏窗口
        private bool _hotRegistered;
        private string _hotName = "";
        private bool _winHotRegistered;
        private string _winHotName = "";

        private NotifyIcon _tray;               // 托盘图标（任务栏不显示图标时用它把窗口找回来）
        private ContextMenuStrip _trayMenu;

        internal const string ShowEventName = "Chat_ShowWindow_Event_v1";
        private EventWaitHandle _showEvent;     // 第二个实例用它来“把已有窗口叫出来”
        private Thread _showWatcher;

        /// <summary>自测用：强制任务栏图标开关（null = 按 config.txt）。</summary>
        internal static bool? TaskbarOverride;
        /// <summary>
        /// 自测用：不要注册真实的全局热键。
        /// 全局热键是系统独占的：自测进程一注册，用户自己那个实例就注册不上，
        /// 用户按 Ctrl+Alt+Q 会弹出自测进程的选择层、截图也挂进自测进程 —— 用户会一头雾水。
        /// 自测改用 PostMessage 直接触发，不需要真的占用系统热键。
        /// </summary>
        internal static bool SkipHotkeyRegistration;

        private bool _reallyExit;        // 只有菜单里的“退出 Chat”才把它置 true
        private bool _closeHintShown;     // 第一次点叉号时提示一次“只是隐藏”

        /// <summary>自测用：不要弹系统通知（气泡提示会打扰正在用电脑的用户）。</summary>
        internal static bool SuppressNotifications;
        /// <summary>自测用：强制窗口透明度百分比（null = 按 config.txt）。</summary>
        internal static int? OpacityOverride;

        internal string LastShowError;      // 自测用：记录唤回窗口时的异常
        internal bool ShowWatcherAlive { get { return _showWatcher != null && _showWatcher.IsAlive; } }
        internal int ShowWakeCount;         // 自测用：watcher 被唤醒的次数

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);   // GW_OWNER = 4
        private const uint GW_OWNER = 4;

        /// <summary>
        /// 把输出框滚到最后一行。
        /// 注意：只读 RichTextBox 上 ScrollToCaret() 并不可靠（实测一行高的框里
        /// 最新内容会停在可视区外），所以再补一条 WM_VSCROLL/SB_BOTTOM。
        /// </summary>
        private void ScrollOutputToEnd()
        {
            try
            {
                _chat.SelectionStart = _chat.TextLength;
                _chat.SelectionLength = 0;
                _chat.ScrollToCaret();
                SendMessage(_chat.Handle, WM_VSCROLL, (IntPtr)SB_BOTTOM, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>
        /// 极简模式专用：直接改写那一行输出（“生成中”“出错”这类临时文字），
        /// 不进入完整对话记录，免得把日志弄花。
        /// </summary>
        private void SetOutputLine(string text, Color color)
        {
            if (_chat == null || _chat.IsDisposed) return;
            // 坑：只读 RichTextBox 上“选中再删”是没用的——_chat.SelectedText = "" 底层走
            //     EM_REPLACESEL，只读控件会忽略它，于是“改写”变成“追加”，
            //     表现就是极简模式下答案显示两遍（流式的全文 + 收尾收敛的那一行）。
            //     用 Clear()（底层 WM_SETTEXT）才真正清得掉。
            if (_chat.TextLength > 0) _chat.Clear();
            _chat.SelectionColor = color;
            _chat.SelectionFont = _fontNormal;
            _chat.AppendText(text == null ? "" : text);
            _chat.SelectionStart = _chat.TextLength;
            _chat.SelectionLength = 0;
            _chat.ScrollToCaret();
        }

        /// <summary>极简模式：本轮第一个字到达时，把“生成中”占位清掉。</summary>
        private void BeginOutputChunk()
        {
            if (_cfg.CompactUI && _firstChunkOfTurn)
            {
                _firstChunkOfTurn = false;
                _chat.Clear();
            }
        }

        /// <summary>
        /// 极简模式收尾：回答结束时，如果那一行停在空行上（模型常以换行结尾），
        /// 就把输出行换成最后一个有内容的行。全文仍在“查看完整对话”里。
        /// </summary>
        private void CompactShowFinalLine()
        {
            string[] lines = Util.SplitLines(_chat.Text);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string s = lines[i].Trim();
                if (s.Length > 0)
                {
                    SetOutputLine(s, Color.FromArgb(20, 20, 20));
                    return;
                }
            }
        }

        /// <summary>把一段委托丢回 UI 线程执行。</summary>
        private void UiPost(MethodInvoker action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOfAny(new char[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }

        // ------------------------------------------------------------------
        //  7.6 UI 自测（Chat.exe --uitest）
        //      真窗口 + 真剪贴板 + 真的 WM_PASTE 消息，最后把窗口截图存档，
        //      用来在没有人工点鼠标的情况下验证“Ctrl+V 贴图”和界面布局。
        // ------------------------------------------------------------------
        internal static int RunUiTest()
        {
            StringBuilder log = new StringBuilder();
            int failed = 0;

            log.AppendLine("Chat UI 自测 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log.AppendLine(new string('-', 60));
            SkipHotkeyRegistration = true;      // 绝不抢用户的全局热键
            SuppressNotifications = true;       // 不弹系统通知，别打扰用户
            try
            {
                failed += RunUiTestForMode("compact", log);
                failed += RunUiTestForMode("full", log);
                log.AppendLine(new string('-', 60));
                log.AppendLine(failed == 0 ? "全部通过 ✔" : ("失败 " + failed + " 项 ✘"));
            }
            catch (Exception ex)
            {
                failed++;
                log.AppendLine("[异常] " + ex);
            }
            finally
            {
                UiModeOverride = null;
                SkipHotkeyRegistration = false;
                SuppressNotifications = false;
                OpacityOverride = null;
                try { File.WriteAllText(Path.Combine(Application.StartupPath, "uitest.log"),
                                        log.ToString(), new UTF8Encoding(true)); }
                catch { }
            }
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// 自测窗口是否还活着。自测窗口是置顶可见的，万一被外部关掉（别人点了它、
        /// 按了 Alt+F4），后续检查就没意义了 —— 这种情况明确记“跳过”，
        /// 不要伪装成程序失败，也不要用一个看不懂的异常把日志弄花。
        /// </summary>
        private static bool TestFormAlive(ChatForm f, StringBuilder log, string where)
        {
            if (!f.IsDisposed && !f.Disposing) return true;
            log.AppendLine("[跳过] 测试窗口在 “" + where + "” 之前被外部关闭，本模式后续检查未执行。");
            return false;
        }

        /// <summary>把某一种界面模式完整跑一遍：贴图、单行/多行结构、越界检查、截图。</summary>
        private static int RunUiTestForMode(string mode, StringBuilder log)
        {
            bool expectCompact = string.Equals(mode, "compact", StringComparison.OrdinalIgnoreCase);
            int failed = 0;
            ChatForm f = null;

            try
            {
                log.AppendLine("");
                log.AppendLine("===== " + mode + " 模式 =====");

                // ---- 准备一张“假截图”放进剪贴板（每种模式都要重放一次）----
                using (Bitmap src = new Bitmap(1200, 700))
                {
                    using (Graphics g = Graphics.FromImage(src))
                    {
                        g.Clear(Color.White);
                        g.FillRectangle(Brushes.OrangeRed, 0, 0, 1200, 120);
                        using (Font big = new Font("Arial", 60, FontStyle.Bold))
                            g.DrawString("TEST IMAGE 1234567890", big, Brushes.White, 30, 20);
                        using (Font small = new Font("Arial", 34))
                            g.DrawString("clipboard -> ctrl+V -> deepseek", small, Brushes.Black, 40, 220);
                        g.FillEllipse(Brushes.SteelBlue, 700, 320, 420, 300);
                    }
                    Clipboard.SetImage(src);
                }

                // ---- 真正把主窗口建出来 ----
                UiModeOverride = mode;
                TaskbarOverride = !expectCompact ? true : false;   // 极简模式测“不占任务栏+托盘”，完整模式测“照旧占任务栏”
                OpacityOverride = expectCompact ? 100 : 70;        // 完整模式顺带测半透明设置是否真的生效
                f = new ChatForm();
                f.TopMost = true;
                f.Show();
                for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(60); }
                // 句柄只取一次：反复访问 Handle 会在窗口被外部关掉时抛异常，
                // 而这里我们更想给出“窗口被关了”这种明确结论。
                IntPtr inputHwnd = f._input.Handle;
                if (!TestFormAlive(f, log, "开始")) return failed;

                bool compact = f._cfg.CompactUI;
                if (compact != expectCompact)
                {
                    failed++;
                    log.AppendLine("[失败] ui=" + mode + " 没生效，实际是 " + (compact ? "compact" : "full"));
                }
                log.AppendLine("DPI 诊断：" + DpiReport(f));
                log.AppendLine("界面模式：" + (compact ? "compact（一行输出 + 一行输入）" : "full（完整界面）") +
                               "，客户区：" + f.ClientSize.Width + "×" + f.ClientSize.Height +
                               "，屏幕：" + Screen.FromControl(f).Bounds.Width + "×" + Screen.FromControl(f).Bounds.Height);
                log.AppendLine("提示词来源：" + f._promptSource + "（" + f._promptChars + " 字）  文件：" + Path.GetFileName(f.PromptPath()));

                // ---- 结构断言：极简模式该藏的必须藏、该单行的必须单行 ----
                bool structOk = compact
                    ? (!f._input.Multiline && !f._sendBtn.Visible && !f._stopBtn.Visible &&
                       !f._clearBtn.Visible && !f._reloadBtn.Visible && !f._promptBtn.Visible &&
                       !f._status.Visible && !f._thinkChk.Visible && !f._imgPanel.Visible)
                    : (f._input.Multiline && f._sendBtn.Visible && f._status.Visible &&
                       f._thinkChk.Visible && f._promptBtn.Visible && !f._imgPanel.Visible);
                log.AppendLine((structOk ? "[通过] " : "[失败] ") + "控件显示状态符合该模式预期" +
                               (compact ? "（按钮/状态栏/勾选框全部隐藏，输入框单行）" : "（按钮/状态栏/勾选框/缩略图齐全）"));
                if (!structOk) failed++;

                int oneLine = f._fontNormal.Height + f.px(12);
                bool heightOk = compact ? (f._chat.Height <= oneLine) : (f._chat.Height > oneLine * 3);
                log.AppendLine(string.Format("{0}输出框高度 {1}px（一行文字 {2}px，一行判定上限 {3}px）",
                               heightOk ? "[通过] " : "[失败] ", f._chat.Height, f._fontNormal.Height, oneLine));
                if (!heightOk) failed++;

                // ---- 模拟 Ctrl+V（编辑框收到 WM_PASTE）----
                SendMessage(inputHwnd, WM_PASTE, IntPtr.Zero, IntPtr.Zero);
                for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(50); }

                bool gotImage = f._pendingImageUrl != null && f._pendingImageUrl.StartsWith("data:image/");
                log.AppendLine((gotImage ? "[通过] " : "[失败] ") + "剪贴板图片被接收：" + f._pendingImageInfo);
                if (!gotImage) failed++;

                bool panelOk = f._imgPanel.Visible && f._imgRemoveBtn.Visible &&
                               (compact ? !f._imgPreview.Visible : f._imgPreview.Visible);
                log.AppendLine((panelOk ? "[通过] " : "[失败] ") +
                               (compact ? "有图片时只显示“移除图片”按钮（无缩略图、无尺寸信息）"
                                        : "完整模式仍然显示缩略图和尺寸信息"));
                if (!panelOk) failed++;

                log.AppendLine(string.Format("       诊断(贴图后)：输入框已释放={0} 窗体已释放={1} 截图按钮已释放={2} 移除按钮已释放={3}",
                               f._input.IsDisposed, f.IsDisposed, f._shotBtn.IsDisposed, f._imgRemoveBtn.IsDisposed));
                if (!TestFormAlive(f, log, "文本粘贴测试")) return failed;

                // ---- 纯文本粘贴必须照常走文本通道 ----
                //      先彻底清空剪贴板，否则上一张图可能仍被 OLE 延迟渲染留着，
                //      程序会把这次粘贴也当成图片（真机上不会出现这种连续覆盖的场景）
                Clipboard.Clear();
                Application.DoEvents();
                Thread.Sleep(200);
                Clipboard.SetText("纯文本粘贴测试 abc");
                Application.DoEvents();
                Thread.Sleep(200);
                bool clipHasImage = Clipboard.ContainsImage();
                // 直接问程序：这次粘贴它当成图片处理了吗（返回 false 才说明会走文本通道）
                bool judgedAsImage = f.TryPasteClipboardImage();
                log.AppendLine("       剪贴板含图片=" + clipHasImage + "，程序判定为图片=" + judgedAsImage);
                // 再走真实的 WM_PASTE，确认文本确实进得了输入框。
                // 最多重试一次：同一进程连续改写剪贴板时，OLE 偶尔要更久才就绪，
                // 这是测试环境因素，不是程序行为问题。
                bool textOk = false;
                for (int attempt = 0; attempt < 2 && !textOk; attempt++)
                {
                    SendMessage(inputHwnd, WM_PASTE, IntPtr.Zero, IntPtr.Zero);
                    for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(40); }
                    textOk = f._input.Text.IndexOf("纯文本粘贴测试 abc") >= 0;
                }
                log.AppendLine("       输入框内容长度=" + f._input.TextLength);
                log.AppendLine((textOk ? "[通过] " : "[失败] ") + "剪贴板没有图片时，文本照常粘贴");
                if (!textOk) failed++;

                // ---- 界面清空输入，回到“只有一张图”的状态再截图 ----
                f._input.Clear();
                Application.DoEvents();
                //      抓屏前把窗口挪到固定位置并置顶，避免被别的窗口压住、或被屏幕边缘裁掉
                f.WindowState = FormWindowState.Normal;
                // 挪到工作区右下角：抓屏需要窗口可见，但别盖在用户正在看的区域正中
                Rectangle waT = Screen.PrimaryScreen.WorkingArea;
                f.Location = new Point(Math.Max(0, waT.Right - f.Width - f.px(20)),
                                       Math.Max(0, waT.Bottom - f.Height - f.px(20)));
                f.TopMost = true;
                f.Activate();
                Thread.Sleep(500);
                Application.DoEvents();

                string shot = Path.Combine(f._dir, "uitest_" + mode + ".png");
                Rectangle shotRect = CaptureWindow(f, shot);
                if (!CheckShot(shot, shotRect.Size, log, "窗口截图 " + Path.GetFileName(shot))) failed++;

                // ---- 回归测试：极简模式“改写输出行”必须是替换，不能变成追加 ----
                //      只读 RichTextBox 上 SelectedText="" 会被忽略（EM_REPLACESEL 不受理），
                //      曾导致一轮回答显示两遍：流式全文 + 收尾收敛的那一行。
                if (compact)
                {
                    // 极简模式一排三个控件的硬检查：不许重叠、按钮不许被输入框盖住。
                    // （用户报过“移除图片”显示成“除图片”，就是这一类问题）
                    int iIdx = f.Controls.GetChildIndex(f._input);
                    bool zOk = f.Controls.GetChildIndex(f._shotBtn) < iIdx;
                    bool noOverlap = f._input.Right <= f._shotBtn.Left && f._shotBtn.Right <= (f._imgPanel.Visible ? f._imgPanel.Left : f.ClientSize.Width);
                    if (f._imgPanel.Visible)
                        zOk = zOk && f.Controls.GetChildIndex(f._imgPanel) < iIdx;
                    log.AppendLine(string.Format(
                        "{0}输入框/截图/移除图片 三者不重叠且按钮在上层（输入框右={1}，截图左={2}右={3}，移除图片左={4}）",
                        (zOk && noOverlap) ? "[通过] " : "[失败] ",
                        f._input.Right, f._shotBtn.Left, f._shotBtn.Right,
                        f._imgPanel.Visible ? f._imgPanel.Left : -1));
                    if (!zOk || !noOverlap) failed++;

                    // 先把机制本身摆出来：只读 RichTextBox 上“选中再删”会被系统忽略
                    f._chat.Clear();
                    f._chat.AppendText("ABC");
                    f._chat.SelectionStart = 0;
                    f._chat.SelectionLength = f._chat.TextLength;
                    f._chat.SelectedText = "";
                    log.AppendLine("       机制确认：只读输出框执行 SelectedText=\"\" 后内容是 “" + f._chat.Text +
                                   "”（仍是 ABC 就说明这条清空路径无效）");

                    f.SetOutputLine("第一次", Color.Black);
                    f.SetOutputLine("第二次", Color.Black);
                    string afterReplace = f._chat.Text;
                    bool replaceOk = afterReplace == "第二次";
                    log.AppendLine((replaceOk ? "[通过] " : "[失败] ") +
                                   "改写输出行 = 替换而非追加（实际内容：" + afterReplace + "）");
                    if (!replaceOk) failed++;

                    f.SetOutputLine("…生成中", Color.FromArgb(150, 150, 150));
                    f._firstChunkOfTurn = true;
                    f.BeginOutputChunk();
                    f.AppendRun("这是本次回答", Color.FromArgb(20, 20, 20), false, false);
                    f.CompactShowFinalLine();
                    string shown = f._chat.Text;
                    bool onceOk = shown == "这是本次回答";
                    log.AppendLine((onceOk ? "[通过] " : "[失败] ") +
                                   "走完一轮后答案只显示一遍（输出行实际内容：" + shown + "）");
                    if (!onceOk) failed++;

                    // ---- 截图：选择层逻辑 + 淡灰色选框外观（真的截一小块图回来肉眼检查）----
                    using (Bitmap fake = new Bitmap(1000, 640))
                    {
                        using (Graphics g = Graphics.FromImage(fake))
                        {
                            g.Clear(Color.White);
                            g.FillRectangle(Brushes.LightSteelBlue, 0, 0, 1000, 90);
                            using (Font big = new Font("Arial", 28))
                                g.DrawString("screen snapshot for capture test", big, Brushes.Black, 30, 140);
                            g.FillRectangle(Brushes.SeaGreen, 200, 300, 500, 200);
                        }
                        CaptureOverlay ov = new CaptureOverlay(fake, new Point(0, 0));
                        ov.TestMode = true;      // 自测图层不理会真实鼠标/按键
                        ov.Show();
                        Application.DoEvents();
                        ov.TestMouseDown(new Point(150, 200));
                        ov.TestMouseMove(new Point(620, 470));
                        Thread.Sleep(260);
                        Application.DoEvents();

                        // 只抓选框附近一小块，文件小、又能看清淡灰色框
                        Rectangle cap = new Rectangle(ov.Left + 130, ov.Top + 180, 520, 320);
                        string ovShot = Path.Combine(f._dir, "uitest_截图框选.png");
                        using (Bitmap shot2 = new Bitmap(cap.Width, cap.Height))
                        {
                            using (Graphics g2 = Graphics.FromImage(shot2))
                                g2.CopyFromScreen(cap.Left, cap.Top, 0, 0, cap.Size);
                            shot2.Save(ovShot, ImageFormat.Png);
                        }
                        if (!CheckShot(ovShot, cap.Size, log, "截图框选外观 " + Path.GetFileName(ovShot))) failed++;

                        ov.TestMouseUp(new Point(620, 470));
                        Application.DoEvents();
                        bool selOk = ov.Confirmed && ov.Selection.Width == 470 && ov.Selection.Height == 270;
                        log.AppendLine(string.Format("{0}框选结果正确（{1}）：{2}×{3}",
                            selOk ? "[通过] " : "[失败] ", selOk ? "反方向拖拽也要能算对" : "与实际拖拽不符",
                            ov.Selection.Width, ov.Selection.Height));
                        if (!selOk) failed++;
                        ov.Dispose();
                    }
                }

                // ---- 极简模式的关键点：一行高的输出框必须自动滚到最新内容 ----
                if (compact)
                {
                    for (int i = 1; i <= 6; i++)
                        f._chat.AppendText("第 " + i + " 行测试文字，用来检查是否自动滚到最后一行\r\n");
                    f.ScrollOutputToEnd();      // 走和真实流式显示同一个函数
                    Application.DoEvents();
                    int len = f._chat.TextLength;
                    int topChar = f._chat.GetCharIndexFromPosition(new Point(f.px(2), f.px(2)));
                    int bottomChar = f._chat.GetCharIndexFromPosition(
                        new Point(f._chat.ClientSize.Width - f.px(4), f._chat.ClientSize.Height - f.px(2)));
                    bool scrollOk = len > 0 && bottomChar >= len - 2;
                    log.AppendLine(string.Format(
                        "{0}一行输出框自动滚到最新内容（文字长度 {1}，可视区顶端字符 {2}，底端字符 {3}）",
                        scrollOk ? "[通过] " : "[失败] ", len, topChar, bottomChar));
                    if (!scrollOk) failed++;
                }

                // ---- 越界检查：默认尺寸 + 最小尺寸，任何可见控件都不允许出界 ----
                log.AppendLine(string.Format("默认尺寸客户区 {0}×{1}，按钮占 {2} 行",
                               f.ClientSize.Width, f.ClientSize.Height, CountButtonRows(f)));
                if (!LayoutFits(f, log, mode + " 默认尺寸")) failed++;

                f.Size = f.MinimumSize;
                Application.DoEvents();
                Thread.Sleep(450);
                Application.DoEvents();
                if (!LayoutFits(f, log, mode + " 最小尺寸")) failed++;

                string shotMin = Path.Combine(f._dir, "uitest_" + mode + "_min.png");
                Rectangle shotMinRect = CaptureWindow(f, shotMin);
                if (!CheckShot(shotMin, shotMinRect.Size, log, "最小尺寸截图 " + Path.GetFileName(shotMin))) failed++;

                // ---- 任务栏 / 托盘行为（放在最后，因为中间会把窗口藏起来）----
                if (compact)
                {
                    bool trayOk = !f.ShowInTaskbar && f._tray != null && f._tray.Visible;
                    log.AppendLine((trayOk ? "[通过] " : "[失败] ") +
                                   "不占任务栏时，托盘图标自动出现（ShowInTaskbar=" + f.ShowInTaskbar + "）");
                    if (!trayOk) failed++;

                    f.ToggleWindowVisible();
                    Application.DoEvents();
                    bool hiddenOk = !f.Visible;
                    f.ToggleWindowVisible();
                    Application.DoEvents();
                    bool shownOk = f.Visible;
                    log.AppendLine((hiddenOk && shownOk ? "[通过] " : "[失败] ") +
                                   "托盘能隐藏并重新显示窗口（隐藏=" + hiddenOk + "，恢复=" + shownOk + "）");
                    if (!(hiddenOk && shownOk)) failed++;

                    // 隐藏状态下全局热键/截图路径也不能崩
                    f.Hide();
                    Application.DoEvents();
                    log.AppendLine("       隐藏状态检查：窗口已隐藏=" + !f.Visible + "，截图按钮仍可调用=" + (f._shotBtn != null));
                    f.ToggleWindowVisible();
                    Application.DoEvents();

                    // 直接给窗口发 WM_HOTKEY，走真实的热键处理路径（键盘那一段由系统负责）
                    f.Hide();
                    Application.DoEvents();
                    SendMessage(f.Handle, WM_HOTKEY, new IntPtr(HOTKEY_ID_WINDOW), IntPtr.Zero);
                    Application.DoEvents();
                    bool hotShown = f.Visible;
                    SendMessage(f.Handle, WM_HOTKEY, new IntPtr(HOTKEY_ID_WINDOW), IntPtr.Zero);
                    Application.DoEvents();
                    bool hotHidden = !f.Visible;
                    if (hotHidden) { f.ToggleWindowVisible(); Application.DoEvents(); }
                    log.AppendLine((hotShown && hotHidden ? "[通过] " : "[失败] ") +
                                   "显示/隐藏窗口热键有效（按一下显示=" + hotShown + "，再按一下隐藏=" + hotHidden + "）");
                    if (!(hotShown && hotHidden)) failed++;

                    // 单实例信号：第二个实例（再双击一次 exe）用它把窗口叫回来
                    f.Hide();
                    Application.DoEvents();
                    int wakeBefore = f.ShowWakeCount;
                    bool signalOk = false;
                    try
                    {
                        using (EventWaitHandle ev = EventWaitHandle.OpenExisting(ChatForm.ShowEventName))
                            ev.Set();                       // 就是第二个实例会做的事
                        // 等久一点：watcher 线程唤醒 + 投递到 UI 线程需要时间
                        for (int i = 0; i < 60 && !f.Visible; i++) { Application.DoEvents(); Thread.Sleep(50); }
                        signalOk = f.Visible;
                        log.AppendLine("       唤回诊断：watcher线程存活=" + f.ShowWatcherAlive +
                                       "，被唤醒次数 " + wakeBefore + "→" + f.ShowWakeCount +
                                       "，上次唤回异常=" + (f.LastShowError == null ? "无" : f.LastShowError));
                        if (!signalOk)
                        {
                            // 再直接调一次，区分“事件没送到”和“唤回本身失败”
                            f.ShowFromTray();
                            Application.DoEvents();
                            log.AppendLine("       直接调用唤回后：可见=" + f.Visible +
                                           "，异常=" + (f.LastShowError == null ? "无" : f.LastShowError));
                        }
                    }
                    catch (Exception ex) { log.AppendLine("       单实例信号异常：" + ex.Message); }
                    log.AppendLine((signalOk ? "[通过] " : "[失败] ") +
                                   "再双击一次 exe 能把已有窗口叫回来（不新开窗口）");
                    if (!signalOk) failed++;

                    // ---- 复现用户报的场景：窗口隐藏时按热键截图，再把窗口叫回来 ----
                    //      重点查两件事：①有没有残留的多余窗口（看起来像“图标重叠”）；
                    //      ②“不占任务栏”依赖的隐藏 owner 窗口有没有在截图过程中丢掉。
                    f.ClearPendingImage(null);
                    IntPtr ownerInit = GetWindow(f.Handle, GW_OWNER);
                    f.Hide();
                    Application.DoEvents();
                    IntPtr ownerHidden = GetWindow(f.Handle, GW_OWNER);

                    // 注入小尺寸假快照：自测不再真的全屏抓屏、不再遮住用户屏幕
                    bool capSeen = false;
                    using (Bitmap fakeShot = new Bitmap(600, 400))
                    {
                        using (Graphics g = Graphics.FromImage(fakeShot)) g.Clear(Color.LightGray);
                        CaptureOverrideForTest = fakeShot;

                        PostMessage(f.Handle, WM_HOTKEY, new IntPtr(HOTKEY_ID), IntPtr.Zero);  // 异步，别把测试线程卡住
                        // 关键：截图流程会进入 ShowDialog 的嵌套消息循环，普通循环会被卡住出不来，
                        // 所以用 WinForms 定时器——它在嵌套循环里照样会触发——来模拟框选动作。
                        System.Windows.Forms.Timer driver = new System.Windows.Forms.Timer();
                        driver.Interval = 300;
                        driver.Tick += delegate
                        {
                            foreach (Form fm in Application.OpenForms)
                            {
                                CaptureOverlay c2 = fm as CaptureOverlay;
                                if (c2 == null) continue;
                                capSeen = true;
                                c2.TestMouseDown(new Point(f.px(40), f.px(40)));
                                c2.TestMouseMove(new Point(f.px(320), f.px(220)));
                                c2.TestMouseUp(new Point(f.px(320), f.px(220)));
                                driver.Stop();
                                driver.Dispose();
                                return;
                            }
                        };
                        driver.Start();
                        // 注意：PostMessage 之后 _capturing 还是 false（消息还没被处理），
                        // 所以必须先泵够一段时间的消息让截图流程启动，否则会把定时器当场销毁、
                        // 选择层就永远没人替它框选了（这个坑我踩过一次）。
                        for (int i = 0; i < 300; i++)
                        {
                            Application.DoEvents();
                            Thread.Sleep(50);
                            if (i >= 20 && !f._capturing) break;
                        }
                        bool stillCapturing = f._capturing;
                        try { driver.Dispose(); } catch { }
                        for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(50); }
                        CaptureOverrideForTest = null;

                        log.AppendLine(string.Format(
                            "       选择层统计：构造={0} 次，显示={1} 次，最后选区={2}×{3}（自测驱动={4}，流程已收尾={5}）",
                            CaptureOverlay.Constructed, CaptureOverlay.Shown,
                            CaptureOverlay.LastSelectionW, CaptureOverlay.LastSelectionH, capSeen, !stillCapturing));
                        log.AppendLine("       挂上的图片：" + f._pendingImageInfo);

                        // 这是本次修的核心：窗口本来是藏着的，截完必须自动显出来，
                        // 否则用户看不到“图已挂进对话”，还得自己再打开一次（用户报的就是这个流程）。
                        bool autoShown = f.Visible;
                        log.AppendLine((autoShown ? "[通过] " : "[失败] ") +
                                       "窗口隐藏时截图，截完会自动把窗口显示出来（可见=" + autoShown + "）");
                        if (!autoShown) failed++;

                        // 回来了之后画面必须是完整的：图片行要和输入框同一行、且不越界
                        bool panelRowOk = f._imgPanel.Visible && f._imgPanel.Top == f._input.Top &&
                                          f._imgPanel.Left >= f._input.Right;
                        log.AppendLine(string.Format(
                            "{0}截图后的画面没有错位/残影（图片行可见={1}，图片行Y={2} 输入框Y={3}，图片行X={4} 输入框右={5}）",
                            panelRowOk ? "[通过] " : "[失败] ", f._imgPanel.Visible,
                            f._imgPanel.Top, f._input.Top, f._imgPanel.Left, f._input.Right));
                        if (!panelRowOk) failed++;

                        // 抓一张“隐藏态截图之后”的窗口图，用来肉眼确认没有残块/残边框
                        string afterShot = Path.Combine(f._dir, "uitest_截图后界面.png");
                        Rectangle afterRect = CaptureWindow(f, afterShot);
                        if (!CheckShot(afterShot, afterRect.Size, log, "截图后界面 " + Path.GetFileName(afterShot))) failed++;
                    }

                    IntPtr ownerAfter = GetWindow(f.Handle, GW_OWNER);
                    log.AppendLine(string.Format(
                        "       隐藏态截图复现：选择层出现={0}，截图已挂上={1}，残留窗体数={2}，owner 0x{3:X}→0x{4:X}→0x{5:X}",
                        capSeen, f._pendingImageUrl != null, Application.OpenForms.Count,
                        ownerInit.ToInt64(), ownerHidden.ToInt64(), ownerAfter.ToInt64()));

                    bool noLeak = Application.OpenForms.Count == 1;
                    log.AppendLine((noLeak ? "[通过] " : "[失败] ") + "截图流程没有留下多余的窗口（当前窗体数=" +
                                   Application.OpenForms.Count + "）");
                    if (!noLeak) failed++;

                    bool ownerOk = ownerInit != IntPtr.Zero && ownerAfter != IntPtr.Zero;
                    log.AppendLine((ownerOk ? "[通过] " : "[失败] ") +
                                   "截图流程没有丢掉“不占任务栏”依赖的隐藏 owner（这是任务栏图标不重复出现的关键）");
                    if (!ownerOk) failed++;

                    // 顺手把屏幕右下角托盘区截下来：确认图标真的在托盘里。
                    // 必须由本进程（DPI 感知）来抓，否则坐标会被系统虚拟化、位置全错。
                    Rectangle scr = Screen.PrimaryScreen.Bounds;
                    Rectangle trayRect = new Rectangle(scr.Right - f.px(520), scr.Bottom - f.px(48),
                                                       f.px(520), f.px(48));
                    string trayShot = Path.Combine(f._dir, "uitest_托盘区.png");
                    using (Bitmap tb = new Bitmap(trayRect.Width, trayRect.Height))
                    {
                        using (Graphics tg = Graphics.FromImage(tb))
                            tg.CopyFromScreen(trayRect.Left, trayRect.Top, 0, 0, trayRect.Size);
                        tb.Save(trayShot, ImageFormat.Png);
                    }
                    CheckShot(trayShot, trayRect.Size, log, "托盘区截图 " + Path.GetFileName(trayShot));

                    // ---- 叉号行为：隐藏到后台，而不是退出 ----
                    // 注意：必须用 WM_SYSCOMMAND + SC_CLOSE（叉号/Alt+F4 走的就是这条），
                    // 直接发 WM_CLOSE 的话 WinForms 不会把它标记成“用户关闭”，逻辑就走不到隐藏分支。
                    SendMessage(f.Handle, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);
                    for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(50); }
                    bool hidNotExited = !f.Visible && !f.IsDisposed;
                    log.AppendLine((hidNotExited ? "[通过] " : "[失败] ") +
                                   "点叉号只隐藏、不退出（可见=" + f.Visible + "，已释放=" + f.IsDisposed + "）");
                    if (!hidNotExited) failed++;
                    if (!TestFormAlive(f, log, "叉号行为检查")) return failed;

                    bool menuHasExit = false, trayHasExit = false;
                    foreach (ToolStripItem it in f._menu.Items)
                        if (it.Text != null && it.Text.IndexOf("退出") >= 0) menuHasExit = true;
                    if (f._trayMenu != null)
                        foreach (ToolStripItem it in f._trayMenu.Items)
                            if (it.Text != null && it.Text.IndexOf("退出") >= 0) trayHasExit = true;
                    bool exitPathOk = menuHasExit && (f._trayMenu == null || trayHasExit);
                    log.AppendLine((exitPathOk ? "[通过] " : "[失败] ") +
                                   "窗口菜单有退出入口=" + menuHasExit + "，托盘菜单有退出入口=" + trayHasExit);
                    if (!exitPathOk) failed++;

                    f.ToggleWindowVisible();
                    Application.DoEvents();
                }
                else
                {
                    bool taskbarOk = f.ShowInTaskbar && f._tray == null;
                    log.AppendLine((taskbarOk ? "[通过] " : "[失败] ") +
                                   "占任务栏时不再额外占用托盘（ShowInTaskbar=" + f.ShowInTaskbar + "）");
                    if (!taskbarOk) failed++;

                    // window_opacity=70 是否真的落到窗口上（自测强制 70，避免改动用户配置）
                    bool opacityOk = Math.Abs(f.Opacity - 0.7) < 0.02;
                    log.AppendLine(string.Format("{0}窗口透明度设置生效（实际 {1:P0}，期望 70%）",
                        opacityOk ? "[通过] " : "[失败] ", f.Opacity));
                    if (!opacityOk) failed++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                log.AppendLine("[异常] " + mode + " 模式：" + ex);
            }
            finally
            {
                try { if (f != null) { f.TopMost = false; f.Close(); f.Dispose(); } }
                catch { }
                UiModeOverride = null;
                TaskbarOverride = null;
            }
            return failed;
        }

        /// <summary>把窗口区域截下来存成 PNG（用于自动检查界面排版），返回实际抓到的区域。</summary>
        private static Rectangle CaptureWindow(Form f, string path)
        {
            Rectangle r = f.Bounds;
            Rectangle screen = Screen.FromControl(f).Bounds;
            Rectangle wanted = r;
            r = Rectangle.Intersect(r, screen);
            if (r.Width <= 0 || r.Height <= 0) throw new Exception("窗口不在屏幕可见区域内，无法截图。");

            using (Bitmap bmp = new Bitmap(r.Width, r.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size);
                bmp.Save(path, ImageFormat.Png);
            }
            if (r.Width != wanted.Width || r.Height != wanted.Height)
                throw new Exception(string.Format("窗口被屏幕裁掉了一部分（窗口 {0}×{1}，实际抓到 {2}×{3}），截图不完整。",
                                                  wanted.Width, wanted.Height, r.Width, r.Height));
            return r;
        }

        /// <summary>
        /// 截图自检：文件存在、能解码、尺寸和预期一致。
        /// 比“文件大于 N 字节”靠谱得多 —— 小窗口的截图本来就只有几 KB，
        /// 用字节数当判据会偶发误报（这个坑踩过了）。
        /// </summary>
        private static bool CheckShot(string path, Size expect, StringBuilder log, string what)
        {
            try
            {
                if (!File.Exists(path)) { log.AppendLine("[失败] " + what + "：文件没生成"); return false; }
                using (Bitmap b = new Bitmap(path))
                {
                    if (expect.Width > 0 && (b.Width != expect.Width || b.Height != expect.Height))
                    {
                        log.AppendLine(string.Format("[失败] {0}：尺寸不符，实际 {1}×{2}，预期 {3}×{4}",
                            what, b.Width, b.Height, expect.Width, expect.Height));
                        return false;
                    }
                    log.AppendLine(string.Format("[通过] {0}：{1}×{2}，{3} KB", what, b.Width, b.Height,
                        Math.Round(new FileInfo(path).Length / 1024.0, 1)));
                    return true;
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("[失败] " + what + "：" + ex.Message);
                return false;
            }
        }

        /// <summary>5 个底部按钮占了几行（窄窗口下会自动换行）。</summary>
        private static int CountButtonRows(ChatForm f)
        {
            Button[] btns = new Button[] { f._sendBtn, f._stopBtn, f._clearBtn, f._reloadBtn, f._promptBtn };
            List<int> tops = new List<int>();
            foreach (Button b in btns)
                if (tops.IndexOf(b.Top) < 0) tops.Add(b.Top);
            return tops.Count;
        }

        /// <summary>检查所有可见控件是否都在客户区内（越界 = 会被窗口裁掉，用户就看不见了）。</summary>
        private static bool LayoutFits(ChatForm f, StringBuilder log, string tag)
        {
            int W = f.ClientSize.Width, H = f.ClientSize.Height;
            List<string> bad = new List<string>();

            Control[] cs = new Control[] { f._chat, f._input, f._sendBtn, f._stopBtn, f._clearBtn,
                                           f._reloadBtn, f._promptBtn, f._shotBtn, f._thinkChk,
                                           f._status, f._imgPanel };
            foreach (Control c in cs)
            {
                if (!c.Visible) continue;
                if (c.Left < 0 || c.Top < 0 || c.Right > W || c.Bottom > H)
                    bad.Add(ShortLabel(c) + "[" + c.Bounds + "]");
            }

            if (f._imgPanel.Visible)
            {
                Control[] inner = new Control[] { f._imgPreview, f._imgInfo, f._imgRemoveBtn };
                foreach (Control c in inner)
                    if (c.Visible && (c.Right > f._imgPanel.Width || c.Bottom > f._imgPanel.Height))
                        bad.Add("图片区内 " + ShortLabel(c) + "[" + c.Bounds + "]");
            }

            if (bad.Count == 0)
            {
                log.AppendLine("[通过] 布局未越界（" + tag + "，客户区 " + W + "×" + H + "）");
                return true;
            }
            log.AppendLine("[失败] 布局越界（" + tag + "）：" + string.Join("；", bad.ToArray()));
            return false;
        }

        private static string ShortLabel(Control c)
        {
            string t = (c.Text == null ? "" : c.Text).Replace("\r", " ").Replace("\n", " ");
            if (t.Length > 10) t = t.Substring(0, 10) + "…";
            if (t.Length == 0) t = c.GetType().Name;
            return t;
        }
    }

    // ========================================================================
        //  8. 自检（Chat.exe --selftest）
    //     不弹窗、不碰界面，把结果写进 selftest.log，便于在没有 Key 的情况下
    //     先验证：JSON 格式、图片编码、TLS 通道、SSE 解析。
    // ========================================================================
    internal static class SelfTest
    {
        private static readonly StringBuilder Log = new StringBuilder();
        private static int _failed;

        public static int Run()
        {
            string dir = Application.StartupPath;
            Log.AppendLine("Chat 自检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Log.AppendLine("目录：" + dir);
            Log.AppendLine("运行时：" + Environment.Version + " / OS " + Environment.OSVersion.VersionString);
            Log.AppendLine(new string('-', 60));

            Check("Key 解析【裸写】", delegate
            {
                Assert(Util.ParseApiKey("sk-abc123") == "sk-abc123");
            });
            Check("Key 解析【带前缀+注释+引号+BOM】", delegate
            {
                string t = "\uFEFF# 我的Key\r\n// comment\r\napikey = \"sk-xyz789\"\r\n";
                Assert(Util.ParseApiKey(t) == "sk-xyz789");
            });
            Check("Key 解析【api_key: 冒号形式】", delegate
            {
                Assert(Util.ParseApiKey("api_key: sk-0000") == "sk-0000");
            });
            Check("Key 解析【没填 Key 时必须返回空，不能把空当成有效 Key】", delegate
            {
                Assert(Util.ParseApiKey("apikey =") == null);
                Assert(Util.ParseApiKey("# apikey = sk-xxxx\r\napikey =\r\n") == null);
                Assert(Util.ParseApiKey("") == null);
            });
            Check("JSON 转义【引号/反斜杠/换行/中文/控制字符】", delegate
            {
                string s = "他说：\"你好\"\\路径\r\n\t结束\u0001";
                string e = Util.JsonEscape(s);
                Assert(e.IndexOf("\\\"") >= 0 && e.IndexOf("\\\\") >= 0 && e.IndexOf("\\n") >= 0 && e.IndexOf("\\u0001") >= 0);
                Assert(e.IndexOf("你好") >= 0);
                Dictionary<string, object> back = new JavaScriptSerializer().DeserializeObject("{\"v\":\"" + e + "\"}") as Dictionary<string, object>;
                Assert(back != null && Convert.ToString(back["v"]) == s);
            });
            Check("配置解析【config.txt】", delegate
            {
                string t = "# 注释\r\nmodel = deepseek-flash\r\nthinking=on\r\ntimeout_sec = 99\r\nproxy=http://127.0.0.1:7897\r\n" +
                           "show_in_taskbar = off\r\nscreenshot_hotkey = Shift+F2\r\nwindow_opacity = 70\r\n";
                Dictionary<string, string> map = Util.ParseConfig(t);
                Assert(map.Count == 7 && map["model"] == "deepseek-flash" && map["thinking"] == "on");
                Config cfg = new Config();
                Util.ApplyConfigMap(cfg, map);
                Assert(cfg.Model == "deepseek-flash" && cfg.Thinking && cfg.TimeoutSec == 99 && cfg.Proxy == "http://127.0.0.1:7897");
                Assert(!cfg.ShowTaskbarIcon, "show_in_taskbar=off 应解析成不在任务栏显示");
                Assert(cfg.ScreenshotHotkey == "Shift+F2", "截图快捷键应被读到");
                Assert(cfg.WindowOpacity == 70, "透明度应被读到");
            });
            Check("窗口透明度范围保护【低于 20% 会把自己藏没，必须兜住】", delegate
            {
                Assert(Util.ClampOpacity(0) == 20, "0 应被抬到下限 20");
                Assert(Util.ClampOpacity(19) == 20);
                Assert(Util.ClampOpacity(20) == 20);
                Assert(Util.ClampOpacity(65) == 65);
                Assert(Util.ClampOpacity(100) == 100);
                Assert(Util.ClampOpacity(180) == 100, "超过 100 应被压到 100");
                Assert(Util.ClampOpacity(-30) == 20, "负数应被抬到下限");
            });
            Check("托盘图标【自己画的图标能生成，不依赖外部 ico】", delegate
            {
                using (Icon ic = ChatForm.MakeTrayIcon())
                {
                    Assert(ic != null && ic.Width >= 16, "托盘图标生成失败");
                    Log.AppendLine("       图标尺寸：" + ic.Width + "×" + ic.Height);
                }
            });
            Check("文本编码识别【UTF-8 / UTF-8+BOM / UTF-16 / GBK 老记事本】", delegate
            {
                string sample = "你是我的助手，请先给结论。";
                byte[] utf8bom = Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes(sample));
                byte[] utf16 = Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes(sample));
                byte[] gbk = Encoding.GetEncoding(936).GetBytes(sample);
                Assert(Util.DecodeBytesAuto(Encoding.UTF8.GetBytes(sample)) == sample, "纯 UTF-8 读取失败");
                Assert(Util.DecodeBytesAuto(utf8bom) == sample, "UTF-8(带BOM) 读取失败");
                Assert(Util.DecodeBytesAuto(utf16) == sample, "UTF-16 读取失败");
                Assert(Util.DecodeBytesAuto(gbk) == sample, "GBK(老记事本默认) 读取失败——中文会变乱码");
                Assert(Util.DecodeBytesAuto(new byte[0]) == "");
            });
            Check("提示词优先级【文件 > config.txt > 内置默认】", delegate
            {
                string src;
                Assert(Util.ResolvePrompt("配置里的", "文件里的", "内置的", out src) == "文件里的" && src == "提示词文件");
                Assert(Util.ResolvePrompt("配置里的", null, "内置的", out src) == "配置里的" && src == "config.txt");
                Assert(Util.ResolvePrompt(null, null, "内置的", out src) == "内置的" && src == "内置默认");
                // 文件里只剩下空白/换行 = 当作没写，不能把空提示词顶上去
                Assert(Util.ResolvePrompt("配置里的", "   \r\n\t  ", "内置的", out src) == "配置里的");
            });
            Check("历史裁剪【max=3 时实际送出的对话形态】", delegate
            {
                List<Msg> h = new List<Msg>();
                string shapeAtTurn6 = "";
                for (int t = 1; t <= 6; t++)
                {
                    h.Add(new Msg("user", "第" + t + "个问题", null));
                    Util.TrimHistory(h, 3, 1);
                    Assert(h.Count <= 3, "第 " + t + " 轮超过上限：" + h.Count);
                    Assert(h[h.Count - 1].Role == "user", "最后一条必须是本轮问题");
                    if (t == 6)
                        shapeAtTurn6 = string.Join(",", h.ConvertAll(delegate(Msg m) { return m.Role; }).ToArray());
                    h.Add(new Msg("assistant", "第" + t + "个回答", null));
                    Util.TrimHistory(h, 3, 1);
                }
                Log.AppendLine("       第 6 轮送出的历史：" + shapeAtTurn6);
                Assert(shapeAtTurn6 == "user,assistant,user", "第 6 轮实际形态是 " + shapeAtTurn6);
            });
            Check("每轮必带系统提示词【历史被裁到只剩 1 条也照样在第一条】", delegate
            {
                Config cfg = new Config();
                cfg.SystemPrompt = "这是测试用的提示词内容";
                DeepSeekClient c = new DeepSeekClient(cfg, "sk-test");
                List<Msg> h = new List<Msg>();
                for (int i = 0; i < 20; i++)
                    h.Add(new Msg(i % 2 == 0 ? "user" : "assistant", "内容" + i, null));
                Util.TrimHistory(h, 1, 1);
                Assert(h.Count == 1, "裁剪后应只剩 1 条");
                string json = c.BuildRequestBody(h);
                Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                object[] arr = root["messages"] as object[];
                Assert(arr != null && arr.Length == 2, "应是 1 条 system + 1 条历史，实际 " + (arr == null ? -1 : arr.Length));
                Dictionary<string, object> sys = arr[0] as Dictionary<string, object>;
                Assert(Convert.ToString(sys["role"]) == "system", "第一条不是 system");
                Assert(Convert.ToString(sys["content"]) == "这是测试用的提示词内容", "system 内容不对");
            });
            Check("加强提示词【reinforce_prompt=on 时把设定贴到提问里，off 时不贴】", delegate
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                List<Msg> h = new List<Msg>();
                h.Add(new Msg("user", "第三个问题", null));

                Config off = new Config();
                off.SystemPrompt = "设定A";
                off.ReinforcePrompt = false;
                Dictionary<string, object> r1 = js.DeserializeObject(
                    new DeepSeekClient(off, "sk-x").BuildRequestBody(h)) as Dictionary<string, object>;
                object[] a1 = r1["messages"] as object[];
                string last1 = Convert.ToString(((Dictionary<string, object>)a1[a1.Length - 1])["content"]);
                Assert(last1 == "第三个问题", "off 时不应改动提问内容，实际：" + last1);

                Config on = new Config();
                on.SystemPrompt = "设定B";
                on.ReinforcePrompt = true;
                Dictionary<string, object> r2 = js.DeserializeObject(
                    new DeepSeekClient(on, "sk-x").BuildRequestBody(h)) as Dictionary<string, object>;
                object[] a2 = r2["messages"] as object[];
                string last2 = Convert.ToString(((Dictionary<string, object>)a2[a2.Length - 1])["content"]);
                Assert(last2.IndexOf("第三个问题") == 0, "on 时应保留原提问");
                Assert(last2.IndexOf("设定B") > 0, "on 时应把设定贴进去");
                // 图片消息（content 是数组）也必须能被解析器接受
                List<Msg> hi = new List<Msg>();
                hi.Add(new Msg("user", "看图", "data:image/png;base64,AAAA"));
                string json3 = new DeepSeekClient(on, "sk-x").BuildRequestBody(hi);
                Dictionary<string, object> r3 = js.DeserializeObject(json3) as Dictionary<string, object>;
                Assert(r3 != null, "带图 + 加强提示词时请求体必须仍是合法 JSON");
            });
            Check("截图快捷键解析【大小写/功能键/非法写法】", delegate
            {
                uint m, v; string n;
                Assert(Util.ParseHotkey("ctrl+alt+q", out m, out v, out n) && n == "Ctrl+Alt+Q" && v == 'Q',
                       "小写写法应被接受并规范成 Ctrl+Alt+Q");
                Assert(Util.ParseHotkey("Shift+F2", out m, out v, out n) && n == "Shift+F2" && v == 0x71,
                       "F2 应解析成 VK_F2");
                Assert(Util.ParseHotkey("Ctrl+Alt+5", out m, out v, out n) && n == "Ctrl+Alt+5", "数字键应可用");
                // 不带修饰键必须拒绝：否则会把普通按键全系统抢走
                Assert(!Util.ParseHotkey("Q", out m, out v, out n), "裸按键必须拒绝");
                Assert(!Util.ParseHotkey("Ctrl+", out m, out v, out n), "残缺写法必须拒绝");
                Assert(!Util.ParseHotkey("Ctrl+Alt+QQ", out m, out v, out n), "两个主键必须拒绝");
                Assert(!Util.ParseHotkey("Ctrl+Alt+F99", out m, out v, out n), "越界功能键必须拒绝");
            });
            Check("图片编码【缩放大图 + 生成 data URL】", delegate
            {
                using (Bitmap big = new Bitmap(2400, 900))
                {
                    using (Graphics g = Graphics.FromImage(big))
                    {
                        g.Clear(Color.White);
                        g.DrawString("测试图片 TEST 12345", new Font("Arial", 48), Brushes.Black, 40, 120);
                        g.FillEllipse(Brushes.DodgerBlue, 900, 300, 700, 400);
                    }
                    string info;
                    string url = ImageCodec.ToDataUrl(big, out info);
                    Assert(url != null && url.StartsWith("data:image/"));
                    byte[] raw = Convert.FromBase64String(url.Substring(url.IndexOf("base64,") + 7));
                    Log.AppendLine("       " + info + "，base64 长度 " + raw.Length + " 字节");
                    Assert(raw.Length > 1000);
                }
            });
            Check("请求体【图片消息结构 + 可被 JSON 解析器接受】", delegate
            {
                Config cfg = new Config();
                cfg.Thinking = true;
                DeepSeekClient c = new DeepSeekClient(cfg, "sk-test");
                List<Msg> msgs = new List<Msg>();
                msgs.Add(new Msg("user", "第一轮", null));
                msgs.Add(new Msg("assistant", "回答一", null));
                msgs.Add(new Msg("user", "这张图说了什么？", "data:image/png;base64,AAAA"));
                string json = c.BuildRequestBody(msgs);
                Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                Assert(root != null && Convert.ToString(root["model"]) == "deepseek-flash");
                Assert(Convert.ToString(root["stream"]).ToLowerInvariant() == "true");
                object[] arr = root["messages"] as object[];
                Assert(arr != null && arr.Length == 4);   // system + 3 条
                Dictionary<string, object> last = arr[3] as Dictionary<string, object>;
                object[] parts = last["content"] as object[];
                Assert(parts != null && parts.Length == 2);
                Dictionary<string, object> imgPart = parts[1] as Dictionary<string, object>;
                Assert(Convert.ToString(imgPart["type"]) == "image_url");
            });
            Check("SSE 解析【思考片段 + 回答片段 + [DONE]】", delegate
            {
                string[] lines = new string[] {
                    ": keep-alive",
                    "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"先算一下\"}}]}",
                    "",
                    "data: {\"choices\":[{\"delta\":{\"content\":\"答案是\"}}]}",
                    "data: {\"choices\":[{\"delta\":{\"content\":\"42\"}}]}",
                    "data: [DONE]"
                };
                JavaScriptSerializer js = new JavaScriptSerializer();
                StringBuilder r = new StringBuilder(), a = new StringBuilder();
                foreach (string line in lines)
                {
                    if (line.Length == 0 || line[0] == ':' || !line.StartsWith("data:")) continue;
                    string p = line.Substring(5).Trim();
                    if (p == "[DONE]") break;
                    string rr, cc;
                    DeepSeekClient.TryReadDelta(js, p, out rr, out cc);
                    r.Append(rr); a.Append(cc);
                }
                Assert(r.ToString() == "先算一下" && a.ToString() == "答案是42");
            });
            Check("接口连通性【TLS1.2 + 端点 + 鉴权路径】", delegate
            {
                Config cfg = new Config();
                DeepSeekClient c = new DeepSeekClient(cfg, "sk-invalid-key-for-selftest");
                Exception captured = null;
                try
                {
                    string r;
                    c.Chat(new List<Msg>(new Msg[] { new Msg("user", "ping", null) }), null, out r);
                }
                catch (Exception ex) { captured = ex; }

                if (captured == null)
                {
                    Log.AppendLine("       接口返回成功（说明 Key 有效）");
                    return;
                }
                string m = captured.Message;
                Log.AppendLine("       " + m.Replace("\r", " ").Replace("\n", " "));
                // 用假 Key 时，期望服务器返回 401；能拿到 401 就证明网络/TLS/请求体都通了
                Assert(m.IndexOf("401") >= 0, "预期 401（假 Key 被拒），实际：" + m);
            });

            Log.AppendLine(new string('-', 60));
            Log.AppendLine(_failed == 0 ? "全部通过 ✔" : ("失败 " + _failed + " 项 ✘"));
            try { File.WriteAllText(Path.Combine(dir, "selftest.log"), Log.ToString(), new UTF8Encoding(true)); }
            catch { }
            Console.WriteLine(Log.ToString());
            return _failed == 0 ? 0 : 1;
        }

        private static void Check(string name, MethodInvoker body)
        {
            try
            {
                body();
                Log.AppendLine("[通过] " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Log.AppendLine("[失败] " + name + " → " + ex.Message);
            }
        }

        private static void Assert(bool cond) { Assert(cond, "断言失败"); }

        private static void Assert(bool cond, string message)
        {
            if (!cond) throw new Exception(message);
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }

    // ========================================================================
    //  9. 入口
    // ========================================================================
    //  -----------------------------------------------------------------------
    //  9.5 请求体导出（Chat.exe --dumprequest）
    //      用真实的 apikey.txt / config.txt / 提示词.txt 生成“程序这一轮实际会发出去”
    //      的内容并写到 请求体导出.txt，用来回答这个疑问：
    //      “我的提示词到底有没有被送出去？”——不用猜，直接看文件。
    //  -----------------------------------------------------------------------
    internal static class RequestDump
    {
        public static int Run()
        {
            string dir = Application.StartupPath;
            string outPath = Path.Combine(dir, "请求体导出.txt");
            StringBuilder report = new StringBuilder();

            Config cfg = new Config();
            Dictionary<string, string> map = null;
            string cfgText = Util.ReadTextFile(Path.Combine(dir, "config.txt"));
            if (cfgText != null)
            {
                try { map = Util.ParseConfig(cfgText); Util.ApplyConfigMap(cfg, map); }
                catch { }
            }

            string cfgPromptRaw = (map != null && map.ContainsKey("system_prompt")) ? map["system_prompt"] : null;
            string promptPath = Path.Combine(dir, "提示词.txt");
            string promptFile = Util.ReadTextFile(promptPath);
            string src;
            string effective = Util.ResolvePrompt(cfgPromptRaw, promptFile, Config.DefaultSystemPrompt, out src);
            cfg.SystemPrompt = effective;

            // 按你配置的历史上限，模拟聊到第 6 轮时实际会送出去的消息列表
            List<Msg> history = new List<Msg>();
            const int Turns = 6;
            for (int t = 1; t <= Turns; t++)
            {
                history.Add(new Msg("user", "第 " + t + " 个问题", null));
                Util.TrimHistory(history, cfg.MaxHistoryMessages, cfg.KeepImageTurns);
                if (t == Turns) break;                 // 最后一轮停在“刚问完”的状态
                history.Add(new Msg("assistant", "第 " + t + " 个回答", null));
                Util.TrimHistory(history, cfg.MaxHistoryMessages, cfg.KeepImageTurns);
            }

            DeepSeekClient client = new DeepSeekClient(cfg, "sk-假Key仅用于导出请求体");
            string json = client.BuildRequestBody(history);

            StringBuilder roles = new StringBuilder();
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> root = js.DeserializeObject(json) as Dictionary<string, object>;
                object[] arr = root == null ? null : root["messages"] as object[];
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        Dictionary<string, object> m = arr[i] as Dictionary<string, object>;
                        if (i > 0) roles.Append(" → ");
                        roles.Append(Convert.ToString(m["role"]));
                    }
            }
            catch { }

            report.AppendLine("Chat 请求体导出  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("（这是按你当前配置生成的样本，看完可以直接删；程序不会自动覆盖它）");
            report.AppendLine(new string('=', 74));
            report.AppendLine("配置：模型 " + cfg.Model + "，思考模式 " + (cfg.Thinking ? "开" : "关") +
                              "，max_history_messages = " + cfg.MaxHistoryMessages +
                              "，reinforce_prompt = " + (cfg.ReinforcePrompt ? "on" : "off") +
                              "，界面 " + cfg.Ui);
            report.AppendLine("提示词来源：" + src + "（" + (effective == null ? 0 : effective.Length) + " 字）");
            report.AppendLine(new string('-', 74));
            report.AppendLine("系统提示词原文（每一轮都会作为第一条 system 消息发出去）：");
            report.AppendLine(effective);
            report.AppendLine(new string('-', 74));
            report.AppendLine("模拟第 6 轮提问时，messages 的顺序（1 条 system + " + history.Count + " 条历史）：");
            report.AppendLine("[" + roles + "]");
            report.AppendLine("说明：system 永远是第一条（就是你的提示词），max_history_messages 只管后面那部分留几条。");
            report.AppendLine(new string('-', 74));
            report.AppendLine("完整请求体（Authorization 头不在里面，Key 不落盘）：");
            report.AppendLine(json);

            try { File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Console.WriteLine("写文件失败：" + ex.Message); return 1; }

            Console.WriteLine("已导出：" + outPath);
            Console.WriteLine("提示词来源：" + src + "（" + (effective == null ? 0 : effective.Length) + " 字）");
            Console.WriteLine("messages 顺序：[system] → [" + roles + "]");
            return 0;
        }
    }

    internal static class Program
    {
        private static Mutex _instanceMutex;
        private const string InstanceMutexName = "Chat_SingleInstance_v1";

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            bool selftest = false;
            bool uitest = false;
            bool dump = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) selftest = true;
                    if (string.Equals(a, "--uitest", StringComparison.OrdinalIgnoreCase)) uitest = true;
                    if (string.Equals(a, "--dumprequest", StringComparison.OrdinalIgnoreCase)) dump = true;
                }
            }

            if (dump)
            {
                Environment.ExitCode = RequestDump.Run();
                return;
            }

            if (selftest)
            {
                Environment.ExitCode = SelfTest.Run();
                return;
            }

            try { SetProcessDPIAware(); } catch { }   // 高分屏上字不糊
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (uitest)
            {
                // 弹出一个真窗口跑粘贴测试，跑完自动关掉
                Environment.ExitCode = ChatForm.RunUiTest();
                return;
            }

            // ---- 单实例：第二次双击 exe 只把已有窗口叫出来，不再开一个 ----
            // 不加这个的话，用户找不到窗口时会双击出第二个实例：两个窗口、两个托盘图标、
            // 还会互相抢全局热键，行为难以解释。
            bool createdNew = true;
            try { _instanceMutex = new Mutex(true, InstanceMutexName, out createdNew); }
            catch { createdNew = true; }
            if (!createdNew)
            {
                try
                {
                    using (EventWaitHandle ev = EventWaitHandle.OpenExisting(ChatForm.ShowEventName))
                        ev.Set();
                }
                catch { }
                return;
            }

            try
            {
                Application.Run(new ChatForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序启动失败：\r\n" + ex, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
