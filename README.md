MiIsland - 米家智能设备控制插件



\*\*MiIsland\*\* 是一款为 \[ClassIsland](https://github.com/ClassIsland/ClassIsland) 打造的插件，允许你在课堂大屏上直接控制米家智能设备。你可以将常用设备添加到主界面组件，也可以结合课表时间自动触发设备动作（如下课自动关灯、放学自动关闭空调等）。



> ⚠️ 本项目处于开发初期，部分功能可能尚未稳定，欢迎反馈和贡献代码。



✨ 功能特性



- 🏠 \*\*设备控制\*\*  

支持米家生态中的灯、风扇、空调、插座等设备的基本开关和控制模式切换。



- 📌 \*\*主界面组件\*\*  

提供一个可拖拽到 ClassIsland 主界面的组件，显示你指定的设备快捷开关。



- ⏰ \*\*自动化触发\*\*  

 结合课表事件（上课、下课、放学）或自定义计时器，自动执行设备动作。例如：

上课时自动将灯光调至“自习模式”；

放学后自动关闭所有设备。



- 🔐 \*\*安全授权\*\*  

使用米家账号授权（设备 Token 模式），所有凭证仅存储在本地。



- 📦 安装方法



方式一：通过 ClassIsland 插件市场（推荐）



1. 打开 ClassIsland → 应用设置 → 插件。

2. 进入“插件市场”，搜索 \*\*MiIsland\*\*。

3. 点击“安装”，等待下载完成即可。



方式二：手动安装



1. 从 \[Releases](https://github.com/yty16/MiIsland/releases) 页面下载最新的 `.cipx` 文件。

2. 在 ClassIsland 中打开“插件管理” → 更多设置 → “从本地安装”。

3. 选择下载的 `.cipx` 文件，完成安装。



- 🚀 快速上手



1. 配置米家账号



首次使用时，你需要授权插件访问你的米家设备。



1. 在 ClassIsland 主界面右键托盘图标 → 应用设置 。

2. 找到 MiIsland 插件设置。

3. 通过\[token\_extractor.exe](https://github.com/PiotrMachowski/Xiaomi-cloud-tokens-extractor) 提取Xiaomi Cloud Tokens Extractor。

4. 授权成功后，插件会自动同步你的设备。



> 🔒 授权仅用于读取设备列表和发送控制指令，不会收集任何个人信息。所有通信均经过加密。



2. 添加主界面组件



1. 右键 ClassIsland 托盘图标 → \*\*编辑主界面\*\*。

2. 在底栏点击 \*\*添加组件\*\* → 找到 \*\*MiIsland 设备面板\*\*。

3. 将组件拖拽到主界面上任意位置，或直接点击“+”。

4. 鼠标悬停在组件右上角会出现齿轮图标，点击后可以设置要显示哪些设备。



 3. 设置自动化规则（可选）



1. 进入 MiIsland 插件设置页面，点击 \*\*自动化规则\*\*。

2. 点击“新建规则”，选择触发条件（如：下课铃响起）。

3. 选择要执行的设备动作（如：打开教室风扇）。

4. 保存规则即可。



- 🛠️ 开发计划



- [√] 基础设备开关控制

- [ ] 主界面快捷组件

- [ ] 设备状态实时同步

- [ ] 支持更多米家设备类型（传感器、窗帘等）

- [ ] 场景/一键执行模式

- [ ] 更精细的课表自动化（按科目控制）



 🤝 参与贡献



欢迎提交 [Issue](https://github.com/yty16/MiIsland/issues/new) 和 Pull Request。



 📝 许可证



本项目基于 \*\*LGPL-3.0\*\* 许可证开源。详见 [LICENSE](https://github.com/yty16/MiIsland/blob/main/LICENSE) 文件。



 🙏 致谢



\- \[ClassIsland](https://github.com/ClassIsland/ClassIsland) 提供了优秀的插件框架。

\- \[miot](https://github.com/rytilahti/python-miio) / \[miot-js](https://github.com/ghaiklor/xiaomi-miot) 等项目提供了米家协议参考。



\---



\*\*如果觉得这个插件对你有帮助，可以点亮 Star ⭐ 支持一下！\*\*

