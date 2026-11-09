#!/usr/bin/env node
// ============================================================================
// pckdump.js —— Godot PCK 查看 / 解包工具（友尽尖塔工程自带）
// ----------------------------------------------------------------------------
// 用途：不启动 Godot，直接读 .pck 的文件表，列表 / 统计 / 解包。
//   比交接文档 §2.5 那套"临时 Godot 工程 + load_resource_pack"快得多，
//   适合：查自己的包有没有漏资源、扒别人的 mod、给 PCK 瘦身、
//         以及倒出游戏本体资源（原版中文文案等）。
//
// 用法：
//   node tools/pckdump.js <xxx.pck>                     # 摘要 + 列表（条目多时只给摘要）
//   node tools/pckdump.js <xxx.pck> --all               # 强制列出全部
//   node tools/pckdump.js <xxx.pck> --filter "youjin/"  # 只看匹配的（正则）
//   node tools/pckdump.js <xxx.pck> --extract <目录>    # 解包（配合 --filter 只解一部分）
//   node tools/pckdump.js <xxx.pck> --dups              # 只查重名条目（PCK 瘦身用）
//   node tools/pckdump.js <xxx.pck> --json              # 输出 JSON（给脚本用）
//
// ★ 只做随机读取：不把整个文件读进内存，所以 1.9 GB 的游戏本体 pck 也能直接列。
//
// ⚠ 格式说明（实测 Godot 4.5.1 导出的包）：
//   头部：magic "GDPC"(4) | pack版本(4) | 主/次/补丁版本(各4) | flags(4)
//         | file_base(u64，偏移 24) | 保留 64 字节
//         | （format 3 起）目录偏移在偏移 32 处
//   目录（format 3）：**在文件末尾**，`u32 条目数` + 每项
//         `u32 路径长 + 路径(4 字节对齐) + u64 偏移 + u64 大小 + 16 字节 md5 + u32 flags`
//   文件数据：file_base + 偏移
//   format 2（Godot 4.0~4.3）：条目数在偏移 96，条目紧跟在头部后面（本工具也支持）。
//
// 已知坑（2026-10-15 实测，写在这里免得下次又查一遍）：
//   · 网上不少 PCK 解析代码假设"表在头部之后"，**只对 format 2 成立**；
//     format 3 必须去偏移 32 取目录偏移，否则会读到 0 个文件。
//   · 路径字段是 4 字节对齐的，长度里含补齐的 \0，读出来要 trim。
//   · 同一个文件**可能被导出两次**（真见过：youjin.pck 里 yuai.mp3 存了两份，
//     白白多占 2.52 MB）。用 --dups 查。
//   · C# 脚本（.cs）在包里是 **1 字节占位桩**，不是源码 —— 别指望从包里扒出代码。
// ============================================================================

'use strict';

const fs = require('fs');
const path = require('path');

// ---- 参数解析 ---------------------------------------------------------------

const argv = process.argv.slice(2);
if (argv.length === 0 || argv[0] === '-h' || argv[0] === '--help') {
  console.log(`用法: node tools/pckdump.js <xxx.pck> [选项]

选项:
  --all              列出全部条目（默认条目 > 500 时只给摘要）
  --filter <正则>    只显示 / 只解包路径匹配的条目
  --extract <目录>   把条目解包到该目录（可与 --filter 组合）
  --dups             只报告重名条目（PCK 瘦身用）
  --json             以 JSON 输出（便于脚本处理）
  -h, --help         显示本帮助`);
  process.exit(0);
}

const pckPath = argv[0];
let extractDir = null;
let filter = null;
let showAll = false;
let dupsOnly = false;
let asJson = false;

for (let i = 1; i < argv.length; i++) {
  const a = argv[i];
  if (a === '--all') showAll = true;
  else if (a === '--dups') dupsOnly = true;
  else if (a === '--json') asJson = true;
  else if (a === '--filter') filter = new RegExp(argv[++i]);
  else if (a === '--extract') extractDir = argv[++i];
  else { console.error(`未知参数: ${a}（-h 看用法）`); process.exit(2); }
}

function fail(msg) { console.error(`✗ ${msg}`); process.exit(1); }

// ---- 随机读取（不整file读入内存）--------------------------------------------

const fd = fs.openSync(pckPath, 'r');
const fileSize = fs.fstatSync(fd).size;

function readAt(offset, length) {
  const b = Buffer.alloc(length);
  fs.readSync(fd, b, 0, length, offset);
  return b;
}

if (fileSize < 100) fail('文件太小，不像 PCK');
if (readAt(0, 4).toString('ascii') !== 'GDPC') fail('开头不是 GDPC，不是 Godot PCK');

const head = readAt(0, 112);
const packFormat = head.readUInt32LE(4);
const verMajor = head.readUInt32LE(8);
const verMinor = head.readUInt32LE(12);
const verPatch = head.readUInt32LE(16);
const packFlags = head.readUInt32LE(20);
const fileBase = Number(head.readBigUInt64LE(24));

if (packFlags & 1) fail('这个包的目录是加密的（pack_flags bit0），本工具读不了');
if (fileBase > fileSize) fail(`file_base ${fileBase} 越界`);

// format 3：目录在文件末尾，偏移记在头部偏移 32；format 2：目录紧跟头部
const tablePos = packFormat >= 3 ? Number(head.readBigUInt64LE(32)) : 96;
if (tablePos <= 0 || tablePos >= fileSize) fail(`目录偏移 ${tablePos} 越界`);

const table = readAt(tablePos, fileSize - tablePos);
let p = 0;
const count = table.readUInt32LE(p); p += 4;
if (count > 5_000_000) fail(`条目数 ${count} 明显不合理，格式判断可能有误`);

const entries = [];
for (let i = 0; i < count; i++) {
  if (p + 4 > table.length) fail(`第 ${i} 个条目越界（文件被截断？）`);
  const len = table.readUInt32LE(p); p += 4;
  if (p + len > table.length) fail(`第 ${i} 个条目路径越界`);
  const name = table.toString('utf8', p, p + len).replace(/\0+$/, ''); p += len;
  p = (p + 3) & ~3;                                  // 路径 4 字节对齐
  const offset = Number(table.readBigUInt64LE(p)); p += 8;
  const size = Number(table.readBigUInt64LE(p)); p += 8;
  const md5 = table.toString('hex', p, p + 16); p += 16;
  const flags = table.readUInt32LE(p); p += 4;
  entries.push({ name, offset, size, md5, flags });
}

// ---- 统计 -------------------------------------------------------------------

const dataBytes = entries.reduce((s, e) => s + e.size, 0);
const tableBytes = fileSize - fileBase - dataBytes;
const byName = new Map();
for (const e of entries) {
  if (!byName.has(e.name)) byName.set(e.name, []);
  byName.get(e.name).push(e);
}
const dups = [...byName.entries()].filter(([, v]) => v.length > 1);
const dupWaste = dups.reduce((s, [, v]) => s + v[0].size * (v.length - 1), 0);

const mb = (n) => (n / 1048576).toFixed(2) + ' MB';

if (asJson) {
  console.log(JSON.stringify({
    pck: pckPath, packFormat, godot: `${verMajor}.${verMinor}.${verPatch}`,
    fileBase, entryCount: count, dataBytes, tableBytes, fileSize,
    duplicates: dups.map(([name, v]) => ({ name, count: v.length, size: v[0].size })),
    duplicateWasteBytes: dupWaste,
    entries: entries.map(({ name, offset, size, flags }) => ({ name, offset, size, flags })),
  }, null, 2));
  process.exit(0);
}

console.log(`包: ${pckPath}`);
console.log(`格式: pack v${packFormat}（Godot ${verMajor}.${verMinor}.${verPatch}）  flags=0x${packFlags.toString(16)}`);
console.log(`条目: ${count} 个    数据 ${mb(dataBytes)}    文件表 ${mb(tableBytes)}    pck 总计 ${mb(fileSize)}`);

if (dups.length > 0) {
  console.log('');
  console.log(`⚠ 有 ${dups.length} 个路径被存了多份，白占 ${mb(dupWaste)}：`);
  for (const [name, v] of dups) {
    console.log(`   ${name}  ×${v.length}（每个 ${mb(v[0].size)}）`);
    for (const e of v) console.log(`      偏移 ${e.offset}  大小 ${e.size}`);
  }
}

if (dupsOnly) process.exit(0);

const shown = filter ? entries.filter((e) => filter.test(e.name)) : entries;
if (showAll || filter || shown.length <= 500) {
  console.log('');
  for (const e of shown) {
    console.log(`${String(e.size).padStart(10)}  ${e.md5.slice(0, 8)}  ${e.name}`);
  }
  if (filter) console.log(`\n（--filter 命中 ${shown.length} / ${count} 条）`);
} else {
  console.log(`\n（条目较多，未全部列出；加 --all 列全部，或 --filter "关键字" 筛选）`);
}

// ---- 解包 -------------------------------------------------------------------

if (extractDir) {
  const targets = filter ? entries.filter((e) => filter.test(e.name)) : entries;
  fs.mkdirSync(extractDir, { recursive: true });
  let ok = 0;
  for (const e of targets) {
    if (fileBase + e.offset + e.size > fileSize) { console.error(`✗ 跳过 ${e.name}：数据越界`); continue; }
    // 去掉 res:// 前缀并把 Windows 非法字符换掉
    const rel = e.name.replace(/^res:\/\//, '').replace(/[\\:*?"<>|]/g, '_');
    const dest = path.join(extractDir, rel);
    try {
      fs.mkdirSync(path.dirname(dest), { recursive: true });
      fs.writeFileSync(dest, readAt(fileBase + e.offset, e.size));
      ok++;
    } catch (err) {
      console.error(`✗ 写 ${dest} 失败: ${err.message}`);
    }
  }
  console.log(`\n解包完成：${ok} / ${targets.length} 个文件 → ${extractDir}`);
}

fs.closeSync(fd);
