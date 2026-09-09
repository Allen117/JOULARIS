# JOULARIS 官網

JOULARIS（Joule × Polaris —— 能源的指北星）商品介紹官網。
Astro 純靜態單頁 landing，部署於 Cloudflare Pages。

## 開發

```bash
npm install
npm run dev      # http://localhost:4321
```

## 建置

```bash
npm run build    # 輸出至 dist/
npm run preview  # 本機預覽 build 結果
```

## 部署（Cloudflare Pages）

1. 推上 GitHub repo `joularis-web`
2. Cloudflare Dashboard → Workers & Pages → Create → Pages → 連結該 repo
3. Build 設定：
   - Framework preset：**Astro**
   - Build command：`npm run build`
   - Build output directory：`dist`
4. 部署完成後可用 `*.pages.dev` 網址開啟；自訂網域於 Pages → Custom domains 綁定（SSL 自動簽發）

## 結構

```
src/
├── pages/index.astro        # 單頁入口，組合各區塊
├── layouts/BaseLayout.astro # head / SEO / OG meta / 導覽列
├── components/              # Hero、BrandStory、Features、UseCases、WhyUs、Contact、Footer
└── styles/global.css        # 設計系統（色票抽自 Logo：teal #1E7E8C → 亮藍 #2BA6E0）
public/
├── logo.svg                 # 能源指北星 Logo（頁面使用，支援深色模式）
├── logo.png                 # 同款點陣版（og:image 社群分享預覽用）
└── favicon.png              # 星形圖示的網站小圖示
```

## 待辦

- [x] 以正式 Logo 圖檔覆蓋 `public/logo.png` / `public/favicon.png`
- [ ] `src/components/Contact.astro` 內的 Email / 電話換成正式聯絡資訊
- [ ] 產品示範畫面截圖（行銷 mockup + 虛構資料）替換 Hero 內建示意圖
