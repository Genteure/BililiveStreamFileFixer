# B站直播录像修复工具

本工具已废弃不再更新维护，请使用 [B站录播姬](rec.danmuji.org) ([GitHub](https://github.com/Bililive/BililiveRecorder)) 里的工具箱

---

本工具可以修复一部分B站直播录像的问题。

本工具目前有一些已知问题还未修改：

- 有时切分出来的 flv 文件会缺少 Video Header
  - 从前一个 flv 文件里复制一份
- 有时时间戳会从负数开始，导致B站主站上传视频时提示“找不到视频轨道”
  - 重写时间戳调整逻辑，让他不会出现负数

## LICENSE: GPLv3

B站直播录像修复工具  
Copyright (C) 2020 Genteure

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
