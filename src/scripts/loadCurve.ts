// 背景用電曲線：一天 24 小時、每 15 分鐘一點的負載（0–1 正規化）。
// BEFORE：未控制，早上冰水主機啟動、中午與下午各有尖峰，超過契約容量。
// AFTER：導入後，尖峰削平並移一部分到清晨離峰預冷，最高約 0.66，
// 貼著契約容量下方而不是壓得很低，比較接近實際控制的樣子。

export const POINTS = 97; // 00:00–24:00，含頭尾
export const CONTRACT = 0.7; // 契約容量（正規化）

export const VIEW_W = 1000;
export const VIEW_H = 400;
const TOP_PAD = 40;

const gauss = (t: number, mu: number, sigma: number) =>
  Math.exp(-((t - mu) ** 2) / (2 * sigma ** 2));

const smoothstep = (a: number, b: number, x: number) => {
  const k = Math.min(1, Math.max(0, (x - a) / (b - a)));
  return k * k * (3 - 2 * k);
};

// 固定的擬雜訊，讓曲線像真實電表資料而不是平滑函數
const jitter = (i: number) =>
  Math.sin(i * 1.7) * 0.5 + Math.sin(i * 0.63 + 1.2) * 0.3 + Math.sin(i * 3.1 + 0.4) * 0.2;

const hourOf = (i: number) => (i / (POINTS - 1)) * 24;

export const BEFORE = Array.from({ length: POINTS }, (_, i) => {
  const t = hourOf(i);
  const office = smoothstep(7, 9, t) * (1 - smoothstep(18, 20.5, t));
  return (
    0.22 +
    office * 0.38 +
    gauss(t, 8.6, 0.35) * 0.3 +
    gauss(t, 13.5, 1.3) * 0.18 +
    gauss(t, 15.2, 0.3) * 0.15 +
    jitter(i) * 0.028
  );
});

export const AFTER = Array.from({ length: POINTS }, (_, i) => {
  const t = hourOf(i);
  const office = smoothstep(6.5, 8.5, t) * (1 - smoothstep(18, 20.5, t));
  return (
    0.24 +
    gauss(t, 5.5, 1.2) * 0.12 +
    office * 0.37 +
    gauss(t, 13.5, 1.6) * 0.05 +
    jitter(i) * 0.01
  );
});

const ease = (x: number) => x * x * (3 - 2 * x);

/**
 * 依進度 p（0–1）混合兩條曲線。控制由左往右逐段接手，
 * 所以每個點各自有延遲，看起來像一路削過去。
 * breathe 是即時起伏的微小位移。
 */
export function mix(p: number, breathe: (i: number) => number = () => 0) {
  return BEFORE.map((b, i) => {
    const x = i / (POINTS - 1);
    const local = ease(Math.min(1, Math.max(0, p * 1.4 - x * 0.4)));
    return b + (AFTER[i] - b) * local + breathe(i);
  });
}

export const yOf = (v: number) => VIEW_H - v * (VIEW_H - TOP_PAD);

export function linePath(vals: number[]) {
  return vals
    .map((v, i) => `${i ? 'L' : 'M'}${((i / (POINTS - 1)) * VIEW_W).toFixed(1)} ${yOf(v).toFixed(1)}`)
    .join('');
}

export function areaPath(vals: number[]) {
  return `${linePath(vals)}L${VIEW_W} ${VIEW_H}L0 ${VIEW_H}Z`;
}
