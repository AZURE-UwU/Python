# # pip install pdf2image pillow
# from pdf2image import convert_from_path
# import os

# pdf_path = "实验报告4.pdf"
# output_folder = "output_images"

# # 如果输出文件夹不存在，则创建
# os.makedirs(output_folder, exist_ok=True)

# # 将 PDF 每页转换为图片
# pages = convert_from_path(pdf_path, dpi=600)

# # 保存每页为单独图片
# for i, page in enumerate(pages):
#     image_path = os.path.join(output_folder, f"page_{i+1}.png")
#     page.save(image_path, "PNG")
#     print(f"Saved: {image_path}")


# # pip install pdf2image pillow
# from pdf2image import convert_from_path
# import os
# import tkinter as tk
# from tkinter import filedialog
# from tkinter import messagebox

# def pdf_to_images():
#     # 创建文件选择窗口
#     root = tk.Tk()
#     root.withdraw()  # 隐藏主窗口
#     pdf_path = filedialog.askopenfilename(
#         title="选择 PDF 文件",
#         filetypes=[("PDF 文件", "*.pdf")]
#     )

#     if not pdf_path:
#         print("未选择文件")
#         return

#     # 在 PDF 所在路径下新建文件夹
#     pdf_dir = os.path.dirname(pdf_path)
#     pdf_name = os.path.splitext(os.path.basename(pdf_path))[0]
#     output_folder = os.path.join(pdf_dir, pdf_name + "_images")
#     os.makedirs(output_folder, exist_ok=True)

#     # 将 PDF 每页转换为图片
#     pages = convert_from_path(pdf_path, dpi=600)

#     # 保存每页为单独图片
#     for i, page in enumerate(pages):
#         image_path = os.path.join(output_folder, f"page_{i+1}.png")
#         page.save(image_path, "PNG")
#         print(f"Saved: {image_path}")

# if __name__ == "__main__":
#     pdf_to_images()


import fitz  # PyMuPDF
import os
import tkinter as tk
from tkinter import filedialog, messagebox

def pdf_to_images():
    root = tk.Tk()
    root.withdraw()

    pdf_path = filedialog.askopenfilename(
        title="选择 PDF 文件",
        filetypes=[("PDF 文件", "*.pdf")]
    )
    if not pdf_path:
        return

    pdf_dir = os.path.dirname(pdf_path)
    pdf_name = os.path.splitext(os.path.basename(pdf_path))[0]
    output_folder = os.path.join(pdf_dir, pdf_name + "_images")
    os.makedirs(output_folder, exist_ok=True)

    doc = fitz.open(pdf_path)

    for i, page in enumerate(doc):
        zoom = 4  # 放大倍数，越大越清晰（2=200%，4=400%）
        mat = fitz.Matrix(zoom, zoom)
        pix = page.get_pixmap(matrix=mat)

        image_path = os.path.join(output_folder, f"page_{i+1}.png")
        pix.save(image_path)
        print("Saved:", image_path)

    messagebox.showinfo("完成", f"PDF 已转换为图片，保存在:\n{output_folder}")

if __name__ == "__main__":
    pdf_to_images()
