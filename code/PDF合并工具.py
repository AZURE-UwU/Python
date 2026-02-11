import os
import tkinter as tk
from tkinter import filedialog, messagebox
from pypdf import PdfReader, PdfWriter
from PIL import Image

pdf_files = []
image_files = []

def clear_file_list():
    pdf_files.clear()
    image_files.clear()
    file_list.delete(0, tk.END)


def select_pdfs():
    files = filedialog.askopenfilenames(
        title="选择 PDF 文件",
        filetypes=[("PDF 文件", "*.pdf")]
    )
    if files:
        pdf_files.clear()
        pdf_files.extend(files)
        update_file_list()

def select_images():
    files = filedialog.askopenfilenames(
        title="选择图片文件",
        filetypes=[("图片文件", "*.png;*.jpg;*.jpeg;*.bmp")]
    )
    if files:
        image_files.clear()
        image_files.extend(files)
        update_file_list()

def update_file_list():
    file_list.delete(0, tk.END)
    for f in pdf_files:
        file_list.insert(tk.END, f)
    for f in image_files:
        file_list.insert(tk.END, f + "（将转换为 PDF）")

def convert_images_to_pdf(temp_dir):
    converted_paths = []
    a4_width, a4_height = 1240, 877  # 横向 A4 尺寸（150 DPI）

    for idx, img_path in enumerate(image_files):
        img = Image.open(img_path).convert("RGB")

        # 缩放图片以适应 A4 横向尺寸，保持比例
        img.thumbnail((a4_width, a4_height), Image.Resampling.LANCZOS)

        # 创建白色背景 A4 横向画布
        background = Image.new("RGB", (a4_width, a4_height), (255, 255, 255))

        # 计算居中位置
        x = (a4_width - img.width) // 2
        y = (a4_height - img.height) // 2
        background.paste(img, (x, y))

        # 保存为 PDF
        temp_pdf = os.path.join(temp_dir, f"image_{idx}.pdf")
        background.save(temp_pdf, "PDF")
        converted_paths.append(temp_pdf)

    return converted_paths


def merge_all():
    if not pdf_files and not image_files:
        messagebox.showwarning("警告", "请至少选择一个 PDF 或图片文件")
        return

    output_path = filedialog.asksaveasfilename(
        title="保存合并后的 PDF",
        defaultextension=".pdf",
        filetypes=[("PDF 文件", "*.pdf")]
    )
    if not output_path:
        return

    try:
        writer = PdfWriter()
        # 合并 PDF 文件
        for path in pdf_files:
            reader = PdfReader(path)
            for page in reader.pages:
                writer.add_page(page)

        # 转换图片为 PDF 并合并
        temp_dir = os.path.dirname(output_path)
        converted = convert_images_to_pdf(temp_dir)
        for path in converted:
            reader = PdfReader(path)
            for page in reader.pages:
                writer.add_page(page)

        with open(output_path, "wb") as f:
            writer.write(f)
        messagebox.showinfo("成功", f"PDF 合并完成：\n{output_path}")
    except Exception as e:
        messagebox.showerror("错误", f"合并失败：{e}")

# 创建窗口
root = tk.Tk()
root.title("PDF & 图片合并工具")
root.geometry("600x450")

file_list = tk.Listbox(root, width=80, height=15)
file_list.pack(pady=10)

btn_frame = tk.Frame(root)
btn_frame.pack()

tk.Button(btn_frame, text="清空列表", command=clear_file_list).pack(side=tk.LEFT, padx=10)
tk.Button(btn_frame, text="选择 PDF 文件", command=select_pdfs).pack(side=tk.LEFT, padx=10)
#tk.Button(btn_frame, text="选择图片文件", command=select_images).pack(side=tk.LEFT, padx=10)
tk.Button(btn_frame, text="合并并保存", command=merge_all).pack(side=tk.LEFT, padx=10)

root.mainloop()
