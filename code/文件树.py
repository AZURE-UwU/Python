import os

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
    # 输入目录路径，例如：C:\\Users\\wbw20\\Desktop\\Project\\ESP32\\ESP_Project
    target_dir = input("请输入目录路径: ").strip()
    tree_str = generate_tree(target_dir)

    # 输出到屏幕
    print(tree_str)

    # 保存到文件
    output_file = os.path.join(target_dir, "directory_tree.txt")
    with open(output_file, "w", encoding="utf-8") as f:
        f.write(tree_str)

    print(f"\n文件结构树已保存到: {output_file}")
