# skillbarcap

在线截取施法进度条（cast bar）：

- 构建：`dotnet build -c Release .\\skillbarcap.sln`
- 运行：`\\.\\bin\\Release\\net10.0-windows10.0.19041.0\\SkillbarCapture.exe <hwnd_hex|process_name> [output_folder] [frameCount] [sampleStride]`
  - 启动后会等待施法条出现以定位；定位成功后开始保存帧
  - 不传 `output_folder` 时默认保存到工程目录的 `./castbar`

示例：

- `\\.\\bin\\Release\\net10.0-windows10.0.19041.0\\SkillbarCapture.exe Gw2-64.exe`
