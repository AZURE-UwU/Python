import tkinter as tk
import random
import time
import math

care_messages = [
    "注意休息，别太累啦~", "记得多喝水，乖乖补充能量~", "保持微笑哦，世界会对你眨眨眼~",
    "早点睡觉，做个甜甜的好梦~", "别忘了吃早餐，早安小太阳~", "你很棒的，要相信自己~",
    "保持好心情，阳光正在路上~", "适当运动一下，动一动更开心~", "多和朋友聊聊天，烦恼会变小小的~",
    "别熬夜了，早点抱被子入睡~", "相信未来会更好，奇迹在朝你招手~", "你值得被爱和关心，记得温柔对自己~",
    "保持耐心，温暖会慢慢来到~", "别忘了照顾自己，给自己一个拥抱~", "今天也要加油哦，一步一步来~",
    "偶尔放松一下，给自己充充电~", "你已经很努力了，给自己贴个小星星~", "保持希望，奇迹会发生的~",
    "多出去走走，呼吸新鲜空气，心情会变亮~", "别担心，一切都会慢慢好起来~",
    "小休息一下，软绵绵的惬意在等你~", "喝杯暖饮，暖暖肚子暖暖心~", "笑一笑，烦恼会害羞地躲开~",
    "今天也很可爱，请记得你很重要~", "给自己做件小事，像奖励一样甜~", "不必什么都做得完美，你已经很棒啦~",
    "想念自己的时候，拍拍自己的肩膀~", "难过时允许慢慢来，我在为你加油~", "学会说“不”，保护好温柔的小心灵~",
    "晚上泡脚吧，放松一下好好睡~", "天冷了，多穿一件喜欢的外套~", "做件让自己开心的小事，像撒点糖~",
    "给自己留点时间，静静变得更好~", "声音柔柔地说：你值得被温柔对待~", "别忘了伸个懒腰，身体会跟你说谢谢~",
    "梦想虽然远，但你已经在踏步前进了~", "给自己一个小目标，慢慢实现也甜~", "心情糟糕就吃点好吃的，允许自己宠一宠~",
    "如果紧张，深呼吸三次，世界会慢下来~", "忙碌间隙，眺望窗外，给眼睛放个假~", "失败是学习的糖，尝一尝也没关系~",
    "你有权利慢慢来，节奏就是你的超能力~", "有点累就停下，休息也是生产力哦~", "想哭就哭吧，哭完会更轻松一点~",
    "给自己说声“辛苦了”，你值得被鼓励~", "小小进步也要庆祝，撒花给自己~", "别和别人比，你有独一无二的光~",
    "睡前想三件好事，祝你入梦微笑~", "周末安排点小确幸，生活会更甜~", "今天的你很可爱，明天也会继续可爱~",
    "遇到难题先呼吸，再慢慢拆解它~", "给自己写张小便签，贴上温柔的提示~", "手机放一边，听听喜欢的歌，好好待自己~",
    "吃饭慢一点，细细品味温暖的味道~", "给内心一朵小花，天天浇水会开花~", "别怕尝试，勇敢是你最温柔的力量~",
    "你不是一个人在战斗，我在为你鼓掌~", "感到彷徨时，记得你走过的每一步都算数~", "用一杯热茶温暖手心，也温暖心情~",
    "给自己准备一个小礼物，哪怕只是贴纸~", "在忙里偷闲，做个小小的放空仪式~", "每天进步一点点，未来会对你微笑~",
    "当下的你已经值得被宠爱和认可~", "别忘了对自己温柔点，温柔会回到你身边~", "今天做了就值得表扬哦，给自己一个笑容~",
    "遇到好事记得庆祝，遇到难事记得善待自己~", "闭上眼睛想象一片海，烦恼会随着潮起潮落~", "晚上早点躺下，给梦留个温柔的位置~",
    "学会和自己和解，你会更轻松一些~", "每次尝试都是勇敢的证明，给自己鼓掌~",
]


def random_color_hex():
    """生成随机颜色"""
    return "#{:02X}{:02X}{:02X}".format(
        random.randint(50, 240),
        random.randint(50, 240),
        random.randint(50, 240)
    )

def fade_in(win, step=0.005, delay=1):
    """窗口淡入效果"""
    alpha = win.attributes("-alpha")
    if alpha < 1.0:
        alpha = min(1.0, alpha + step)
        win.attributes("-alpha", alpha)
        win.after(delay, lambda: fade_in(win, step, delay))

def fade_out(win, step=0.05, delay=30, on_destroy=None):
    """窗口淡出效果，可在销毁后执行回调"""
    alpha = win.attributes("-alpha")
    if alpha > 0.0:
        alpha = max(0.0, alpha - step)
        win.attributes("-alpha", alpha)
        win.after(delay, lambda: fade_out(win, step, delay, on_destroy))
    else:
        win.destroy()
        if on_destroy:
            on_destroy()

def create_window(root, message, color, x, y, width=380, height=150):
    """创建一个普通窗口"""
    win = tk.Toplevel(root)
    win.title("❤")
    win.geometry(f"{width}x{height}+{x}+{y}")
    win.configure(bg=color)
    win.attributes("-alpha", 0.0)

    label = tk.Label(win, text=message,
                     bg=color, fg="white", font=("Microsoft YaHei", 14,"bold"))
    label.pack(expand=True, fill="both", padx=10, pady=10)

    fade_in(win)
    return win


def show_final_window(root, screen_w, screen_h):
    """显示最终大窗口"""
    final_win = tk.Toplevel(root)
    final_win.title("❤")
    final_win.attributes("-topmost", True) 
    width, height = 600, 400
    x = (screen_w - width) // 2
    y = (screen_h - height) // 2
    final_win.geometry(f"{width}x{height}+{x}+{y}")
    final_win.configure(bg="pink")
    final_win.attributes("-alpha", 0.0)

    label = tk.Label(final_win, text="要爱自己！",
                     bg="pink", fg="white", font=("Microsoft YaHei", 32, "bold"))
    label.pack(expand=True, fill="both")

    # “不要”按钮
    btn_no = tk.Button(final_win, text="不要!", font=("Microsoft YaHei", 16, "bold"))
    btn_no.place(x=width//2 - 40, y=height//2 + 50)

    def escape(event):
        new_x = random.randint(0, width - 80)
        new_y = random.randint(0, height - 40)
        btn_no.place(x=new_x, y=new_y)

    btn_no.bind("<Enter>", escape)

    # “好！”按钮
    def on_yes():
        label.config(text="好嘟！")
        start_time = time.time()+0.5

        def animate():
            elapsed = time.time() - start_time
            if elapsed > 2:  # 2秒后结束
                final_win.destroy()
                root.quit()
                return

            t = elapsed * 2 
            bounce = -100 * (t - 1)**2 + 100
            new_y = y - int(bounce)

            final_win.geometry(f"{width}x{height}+{x}+{new_y}")

            alpha = max(0.0, 1.0 - elapsed/2.0)
            final_win.attributes("-alpha", alpha)

            final_win.after(30, animate)

        # 延时0.5秒再启动动画
        final_win.after(500, animate)

        
    btn_yes = tk.Button(final_win, text="好！", font=("Microsoft YaHei", 16, "bold"), command=on_yes)
    btn_yes.place(x=width//2 - 40, y=height//2 + 120)

    fade_in(final_win)

# ------------------ 主程序 ------------------
def main():
    root = tk.Tk()
    root.withdraw()  # 隐藏主窗口

    screen_w = root.winfo_screenwidth()
    screen_h = root.winfo_screenheight()

    count = 15
    interval_ms = 600
    close_after_ms = 2000

    # ---------- 第一步：准备所有随机数据 ----------
    used_colors = set()
    window_data = []  # (message, color, x, y)

    for i in range(count):
        color = random_color_hex()
        while color in used_colors:
            color = random_color_hex()
        used_colors.add(color)

        x = random.randint(0, max(0, screen_w - 300))
        y = random.randint(0, max(0, screen_h - 180))

        message = random.choice(care_messages)
        window_data.append((message, color, x, y))

    windows = []

    # ---------- 第二步：循环生成窗口 ----------
    def show_window(i=0):
        if i >= count:
            def close_all_and_show_final():
                for w in windows:
                    fade_out(w) 
                show_final_window(root, screen_w, screen_h)
            root.after(close_after_ms, close_all_and_show_final)
            return

        message, color, x, y = window_data[i]
        win = create_window(root, message, color, x, y)
        windows.append(win)

        root.after(interval_ms, lambda: show_window(i + 1))

    show_window(0)
    root.mainloop()

if __name__ == "__main__":
    main()

# cd "C:\Users\Benedict Wang\Desktop\" 
# pyinstaller --onefile --noconsole 小礼物.py
