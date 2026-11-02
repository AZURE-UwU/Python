import tkinter as tk
from tkinter import ttk
from PIL import Image, ImageTk
import colorsys

# ---------- RGB565 转换函数 ----------
def rgb565_from_components(r5, g6, b5):
    """从 RGB565 分量计算 16-bit 值（整数）"""
    return (r5 << 11) | (g6 << 5) | b5

def components_from_rgb565(value):
    r5 = (value >> 11) & 0x1F
    g6 = (value >> 5) & 0x3F
    b5 = value & 0x1F
    return r5, g6, b5

def r5g6b5_to_8bit(r5, g6, b5):
    """将 5/6/5 分量映射到 8-bit（0-255）"""
    r8 = int(round(r5 * 255 / 31))
    g8 = int(round(g6 * 255 / 63))
    b8 = int(round(b5 * 255 / 31))
    return r8, g8, b8

def rgb8_to_hex(r, g, b):
    return "#{:02x}{:02x}{:02x}".format(r, g, b)

# ---------- GUI ----------
class RGB565Picker(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("RGB565 颜色选择器")
        self.resizable(False, False)
        # 先初始化属性
        self.r5_var = tk.IntVar()
        self.g6_var = tk.IntVar()
        self.b5_var = tk.IntVar()
        self.current_hue = 0.0
        self.create_widgets()

    def create_widgets(self):
        pad = 8
        frame = ttk.Frame(self, padding=pad)
        frame.grid(row=0, column=0)

        # 预览与信息区
        preview_frame = ttk.Frame(frame)
        preview_frame.grid(row=0, column=0, rowspan=6, padx=(0,20))

        self.preview = tk.Canvas(preview_frame, width=160, height=120, bd=1, relief="sunken")
        self.preview.grid(row=0, column=0, pady=(0,10))

        self.hex_label = ttk.Label(preview_frame, text="HEX: #------", font=("Courier New", 10, "bold"))
        self.hex_label.grid(row=1, column=0, sticky="w")
        self.dec_label = ttk.Label(preview_frame, text="RGB565 (dec): 0", font=("Courier New", 10))
        self.dec_label.grid(row=2, column=0, sticky="w")
        self.hex565_label = ttk.Label(preview_frame, text="RGB565 (hex): 0x0000", font=("Courier New", 10))
        self.hex565_label.grid(row=3, column=0, sticky="w")
        self.bin_label = ttk.Label(preview_frame, text="Binary: R=------(31)  G=------(63)  B=------(31)", font=("Courier New", 10))
        self.bin_label.grid(row=4, column=0, sticky="w")
        self.formula_label = ttk.Label(preview_frame, text="Formula: ((R<<11)|(G<<5)|B) = ...", font=("Courier New", 10))
        self.formula_label.grid(row=5, column=0, sticky="w", pady=(2, 8))

        # 滑块区（R5, G6, B5）
        sliders_frame = ttk.Frame(frame)
        sliders_frame.grid(row=0, column=1, sticky="n")

        # R5
        ttk.Label(sliders_frame, text="R (5-bit)").grid(row=0, column=0, sticky="w")
        r5_scale = ttk.Scale(sliders_frame, from_=0, to=31, orient="horizontal")
        r5_scale.set(31)
        r5_scale.config(command=self.on_r5_change)
        r5_scale.grid(row=1, column=0, sticky="we", pady=(0,6))
        self.r5_spin = ttk.Spinbox(sliders_frame, from_=0, to=31, width=5, textvariable=self.r5_var, command=self.on_spin_change)
        self.r5_spin.grid(row=1, column=1, padx=(6,0))

        # G6
        ttk.Label(sliders_frame, text="G (6-bit)").grid(row=2, column=0, sticky="w")
        g6_scale = ttk.Scale(sliders_frame, from_=0, to=63, orient="horizontal")
        g6_scale.set(62)
        g6_scale.config(command=self.on_g6_change)
        g6_scale.grid(row=3, column=0, sticky="we", pady=(0,6))
        self.g6_spin = ttk.Spinbox(sliders_frame, from_=0, to=63, width=5, textvariable=self.g6_var, command=self.on_spin_change)
        self.g6_spin.grid(row=3, column=1, padx=(6,0))

        # B5
        ttk.Label(sliders_frame, text="B (5-bit)").grid(row=4, column=0, sticky="w")
        b5_scale = ttk.Scale(sliders_frame, from_=0, to=31, orient="horizontal")
        b5_scale.set(0)
        b5_scale.config(command=self.on_b5_change)
        b5_scale.grid(row=5, column=0, sticky="we", pady=(0,6))
        self.b5_spin = ttk.Spinbox(sliders_frame, from_=0, to=31, width=5, textvariable=self.b5_var, command=self.on_spin_change)
        self.b5_spin.grid(row=5, column=1, padx=(6,0))

        # 显示 8-bit RGB 值
        self.rgb8_label = ttk.Label(sliders_frame, text="8-bit RGB: (255, 255, 255)")
        self.rgb8_label.grid(row=6, column=0, columnspan=2, pady=(8,0), sticky="w")

        # 色相条（Hue bar）
        hue_frame = ttk.Frame(frame)
        hue_frame.grid(row=0, column=2, padx=(20,0), sticky="n")
        ttk.Label(hue_frame, text="Hue").grid(row=0, column=0)
        self.hue_canvas = tk.Canvas(hue_frame, width=30, height=200, bd=1, relief="sunken")
        self.hue_canvas.grid(row=1, column=0, pady=(6,0))
        self.hue_img = self.make_hue_image(30, 200)
        self.hue_tk = ImageTk.PhotoImage(self.hue_img)
        self.hue_canvas.create_image(0,0, anchor="nw", image=self.hue_tk)
        self.hue_canvas.bind("<Button-1>", self.on_hue_click)
        self.hue_canvas.bind("<B1-Motion>", self.on_hue_click)

        # 颜色渐变（可选，展示当前色相的饱和/明度平面）
        ttk.Label(frame, text="饱和度-明度平面").grid(row=5, column=0, columnspan=3, pady=(12,0), sticky="w")
        self.grad_canvas = tk.Canvas(frame, width=200, height=160, bd=1, relief="sunken", cursor="crosshair")
        self.grad_canvas.grid(row=6, column=0, columnspan=3, pady=(6,0))
        self.grad_canvas.bind("<Button-1>", self.on_gradient_click)
        self.grad_canvas.bind("<B1-Motion>", self.on_gradient_click)
        self.grad_img = None
        self.grad_tk = None
        
        # 初始化显示（所有 widgets 已创建）
        self.update_from_components(31, 62, 0)

    # ---------- 事件与更新 ----------
    def on_r5_change(self, val):
        v = int(float(val))
        self.r5_var.set(v)
        self.update_from_components(v, self.g6_var.get(), self.b5_var.get())

    def on_g6_change(self, val):
        v = int(float(val))
        self.g6_var.set(v)
        self.update_from_components(self.r5_var.get(), v, self.b5_var.get())

    def on_b5_change(self, val):
        v = int(float(val))
        self.b5_var.set(v)
        self.update_from_components(self.r5_var.get(), self.g6_var.get(), v)

    def on_spin_change(self):
        # spinbox 改变时同步
        self.update_from_components(self.r5_var.get(), self.g6_var.get(), self.b5_var.get())

    def on_hue_click(self, event):
        # 根据点击位置读取 hue（0-1），并生成对应颜色（饱和度=1, 明度=1）
        y = max(0, min(self.hue_img.height-1, event.y))
        hue = 1.0 - (y / (self.hue_img.height - 1))  # top = red (h=1), bottom = red (h=0) continuous
        r, g, b = [int(round(c * 255)) for c in colorsys.hsv_to_rgb(hue, 1.0, 1.0)]
        # 将 8-bit 映射到 5/6/5
        r5 = int(round(r * 31 / 255))
        g6 = int(round(g * 63 / 255))
        b5 = int(round(b * 31 / 255))
        self.update_from_components(r5, g6, b5)

    def on_gradient_click(self, event):
        # 根据点击位置读取饱和度和明度，结合当前色相生成颜色
        W, H = 200, 160
        x = max(0, min(W-1, event.x))
        y = max(0, min(H-1, event.y))
        sat = x / (W - 1)
        val = 1.0 - (y / (H - 1))
        r, g, b = [int(round(c * 255)) for c in colorsys.hsv_to_rgb(self.current_hue, sat, val)]
        # 将 8-bit 映射到 5/6/5
        r5 = int(round(r * 31 / 255))
        g6 = int(round(g * 63 / 255))
        b5 = int(round(b * 31 / 255))
        self.update_from_components(r5, g6, b5)

    def update_from_components(self, r5, g6, b5):
        # 更新 spinbox 值（防止循环）
        self.r5_var.set(int(r5))
        self.g6_var.set(int(g6))
        self.b5_var.set(int(b5))

        # 计算 8-bit RGB
        r8, g8, b8 = r5g6b5_to_8bit(r5, g6, b5)
        hex8 = rgb8_to_hex(r8, g8, b8)

        # 计算 RGB565 值
        val = rgb565_from_components(r5, g6, b5)
        hex565 = "0x{:04x}".format(val)
        dec565 = str(val)

        # 二进制拆分
        r_bin = format(r5, '05b')
        g_bin = format(g6, '06b')
        b_bin = format(b5, '05b')

        # 更新 UI
        self.preview.delete("all")
        self.preview.create_rectangle(0,0,160,120, fill=hex8, outline="")
        self.hex_label.config(text=f"HEX: {hex8}")
        self.dec_label.config(text=f"RGB565 (dec): {dec565}")
        self.hex565_label.config(text=f"RGB565 (hex): {hex565}")
        self.bin_label.config(text=f"Binary: R={r_bin:5s}  ({r5:2d})  G={g_bin:6s}  ({g6:2d})  B={b_bin:5s}  ({b5:2d})")
        self.formula_label.config(text=f"Formula: (({r5:2d}<<11)|({g6:2d}<<5)|{b5:2d}) = {dec565}")
        self.rgb8_label.config(text=f"8-bit RGB: ({r8}, {g8}, {b8})")

        # 更新渐变平面（以当前色相为基准）
        # 计算当前色相（从 r8,g8,b8 转到 HSV）
        h, s, v = colorsys.rgb_to_hsv(r8/255.0, g8/255.0, b8/255.0)
        self.update_gradient(h)

    # ---------- 渐变与色相图生成 ----------
    def make_hue_image(self, w, h):
        img = Image.new("RGB", (w, h))
        for y in range(h):
            hue = 1.0 - (y / (h - 1))
            r, g, b = [int(round(c * 255)) for c in colorsys.hsv_to_rgb(hue, 1.0, 1.0)]
            for x in range(w):
                img.putpixel((x, y), (r, g, b))
        return img

    def update_gradient(self, hue):
        # 保存当前色相供点击时使用
        self.current_hue = hue
        # 生成一个 200x160 的饱和度-明度平面（横轴饱和度 0->1，纵轴明度 1->0）
        W, H = 200, 160
        img = Image.new("RGB", (W, H))
        for y in range(H):
            val = 1.0 - (y / (H - 1))
            for x in range(W):
                sat = x / (W - 1)
                r, g, b = [int(round(c * 255)) for c in colorsys.hsv_to_rgb(hue, sat, val)]
                img.putpixel((x, y), (r, g, b))
        self.grad_img = img
        self.grad_tk = ImageTk.PhotoImage(img)
        self.grad_canvas.create_image(0,0, anchor="nw", image=self.grad_tk)

# ---------- 运行 ----------
if __name__ == "__main__":
    app = RGB565Picker()
    app.mainloop()
