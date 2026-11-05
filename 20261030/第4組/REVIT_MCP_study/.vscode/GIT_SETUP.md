# VS Code 設定指南

## Git 整合配置

為了在 VS Code 中正確顯示 Git 版控面板和 Commit 功能，需要以下配置：

### 全域 Git 設定

已設定以下全域 Git 使用者信息：
- 使用者名稱：shuotao
- 電郵：shuotao@users.noreply.github.com

### VS Code 設定

`.vscode/settings.json` 中已配置：
- git.path：指向 Git 執行檔位置
- git.enabled：啟用 Git 整合
- git.autorefresh：自動重新整理 Git 狀態
- git.autofetch：自動取得遠端變更

### 使用步驟

1. 在 VS Code 中開啟本專案資料夾
2. 點擊左邊邊欄的「Source Control」圖示（或按 Ctrl+Shift+G）
3. 應該會看到 Git 版控面板
4. 修改檔案後會在面板中顯示
5. 在 commit message 欄位輸入訊息並提交

### 如果仍未顯示

1. 按 Ctrl + Shift + P 開啟命令面板
2. 輸入「Git: Initialize Repository」
3. 或重新啟動 VS Code
