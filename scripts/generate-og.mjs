// 產生 1200×630 的 OG 分享圖（public/og.png）
// 用法：node scripts/generate-og.mjs
// 素材：src/assets/ems-dashboard.png + 站內品牌色與星形標誌
import sharp from 'sharp';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

const W = 1200;
const H = 630;

// 截圖：先縮到卡片寬度，嵌進 SVG 以便做圓角裁切
const SHOT_W = 640;
const shotPng = await sharp(join(root, 'src/assets/ems-dashboard.png'))
  .resize(SHOT_W * 2) // 2 倍取樣，縮回去時比較銳利
  .png()
  .toBuffer();
const shotB64 = shotPng.toString('base64');
const SHOT_H = Math.round((SHOT_W * 1575) / 2520); // 維持原始比例
const SHOT_X = 590;
const SHOT_Y = 150;

const svg = `<svg width="${W}" height="${H}" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
  <defs>
    <linearGradient id="brand" x1="0" y1="0" x2="1" y2="0">
      <stop offset="0" stop-color="#2E7D32"/>
      <stop offset="0.5" stop-color="#26A69A"/>
      <stop offset="1" stop-color="#1E88E5"/>
    </linearGradient>
    <linearGradient id="star" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#2E7D32"/>
      <stop offset="1" stop-color="#4CAF50"/>
    </linearGradient>
    <radialGradient id="glowG" cx="0.5" cy="0.5" r="0.5">
      <stop offset="0" stop-color="#4CAF50" stop-opacity="0.20"/>
      <stop offset="1" stop-color="#4CAF50" stop-opacity="0"/>
    </radialGradient>
    <radialGradient id="glowB" cx="0.5" cy="0.5" r="0.5">
      <stop offset="0" stop-color="#1E88E5" stop-opacity="0.16"/>
      <stop offset="1" stop-color="#1E88E5" stop-opacity="0"/>
    </radialGradient>
    <clipPath id="shotClip">
      <rect x="${SHOT_X}" y="${SHOT_Y}" width="${SHOT_W}" height="${SHOT_H}" rx="14"/>
    </clipPath>
  </defs>

  <rect width="${W}" height="${H}" fill="#F2F7F5"/>
  <circle cx="1080" cy="40" r="420" fill="url(#glowG)"/>
  <circle cx="120" cy="620" r="420" fill="url(#glowB)"/>

  <!-- 指北星標誌（取自 logo.svg） -->
  <g transform="translate(72,96) scale(0.86)">
    <path fill="url(#star)" d="M57 4 L70 47 L101 60 L70 73 L57 116 L44 73 L13 60 L44 47 Z"/>
    <path fill="#4CAF50" d="M100 12 L104 24 L116 28 L104 32 L100 44 L96 32 L84 28 L96 24 Z"/>
  </g>

  <text x="188" y="188" font-family="Segoe UI, Arial, sans-serif" font-weight="800" font-size="72" letter-spacing="4" fill="#243230">JOULARIS</text>

  <text x="76" y="300" font-family="Microsoft JhengHei, Noto Sans TC, sans-serif" font-weight="700" font-size="52" fill="#243230">幫你找到節能的方向</text>

  <text x="76" y="372" font-family="Microsoft JhengHei, Noto Sans TC, sans-serif" font-size="26" fill="#4c5d59">工商能源管理系統｜電、水、氣整合監控</text>
  <text x="76" y="418" font-family="Microsoft JhengHei, Noto Sans TC, sans-serif" font-size="26" fill="#4c5d59">即時需量追蹤｜AI 節能分析</text>

  <rect x="76" y="470" width="360" height="8" rx="4" fill="url(#brand)"/>

  <!-- 系統截圖卡片 -->
  <rect x="${SHOT_X - 10}" y="${SHOT_Y - 10}" width="${SHOT_W + 20}" height="${SHOT_H + 20}" rx="20" fill="#ffffff" stroke="#d9e7e2"/>
  <g clip-path="url(#shotClip)">
    <image x="${SHOT_X}" y="${SHOT_Y}" width="${SHOT_W}" height="${SHOT_H}" xlink:href="data:image/png;base64,${shotB64}"/>
  </g>
</svg>`;

await sharp(Buffer.from(svg)).png().toFile(join(root, 'public/og.png'));
console.log('public/og.png generated');
