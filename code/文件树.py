import os
import tkinter as tk
from tkinter import filedialog

def print_tree(root, prefix="", output_lines=None):
    if output_lines is None:
        output_lines = []
    try:
        files = os.listdir(root)
    except PermissionError:
        return output_lines

    files.sort()
    for i, f in enumerate(files):
        path = os.path.join(root, f)
        connector = "└── " if i == len(files)-1 else "├── "
        output_lines.append(prefix + connector + f)
        if os.path.isdir(path):
            extension = "    " if i == len(files)-1 else "│   "
            print_tree(path, prefix + extension, output_lines)
    return output_lines

def generate_tree(directory):
    lines = [directory]
    lines.extend(print_tree(directory))
    return "\n".join(lines)

if __name__ == "__main__":
    # 弹出提示框选择目录
    root = tk.Tk()
    root.withdraw()  # 隐藏主窗口
    target_dir = filedialog.askdirectory(title="请选择一个目录")

    if target_dir:
        tree_str = generate_tree(target_dir)

        # 输出到屏幕
        print(tree_str)

        # 保存到文件
        output_file = os.path.join(target_dir, "directory_tree.txt")
        with open(output_file, "w", encoding="utf-8") as f:
            f.write(tree_str)

        print(f"\n文件结构树已保存到: {output_file}")
    else:
        print("未选择目录，程序结束。")
