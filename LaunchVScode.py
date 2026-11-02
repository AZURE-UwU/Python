import subprocess

def launch_vscode():
    # 改成你本机 VS Code 的实际安装路径
    vscode_path = r"C:\Users\wbw20\AppData\Local\Programs\Microsoft VS Code\Code.exe"
    try:
        subprocess.Popen([vscode_path], shell=False)
    except Exception as e:
        print(f"启动 VS Code 出错: {e}")

if __name__ == "__main__":
    launch_vscode()
