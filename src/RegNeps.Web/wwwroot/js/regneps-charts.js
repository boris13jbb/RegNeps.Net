/**
 * Interop Chart.js + descarga de archivos para RegNeps (Blazor Server).
 * Las capturas se comprimen (JPEG escalado) para no saturar SignalR.
 */
window.regnepsCharts = (function () {
  const charts = {};
  const MAX_CAPTURE_WIDTH = 900;
  const JPEG_QUALITY = 0.72;

  function ensureChartJs() {
    if (typeof Chart === "undefined") {
      throw new Error("Chart.js no está cargado.");
    }
  }

  function upsertChart(canvasId, config) {
    ensureChartJs();
    const canvas = document.getElementById(canvasId);
    if (!canvas) {
      return false;
    }

    if (charts[canvasId]) {
      charts[canvasId].destroy();
      delete charts[canvasId];
    }

    charts[canvasId] = new Chart(canvas, config);
    return true;
  }

  function destroyChart(canvasId) {
    const existing = charts[canvasId];
    if (existing) {
      existing.destroy();
      delete charts[canvasId];
    }
  }

  function destroyAll() {
    Object.keys(charts).forEach(destroyChart);
  }

  /** Escala el canvas del chart y exporta JPEG compacto. */
  function captureScaledDataUrl(chart) {
    const src = chart.canvas;
    if (!src) {
      return null;
    }

    const w = src.width || src.clientWidth || 1;
    const h = src.height || src.clientHeight || 1;
    const scale = w > MAX_CAPTURE_WIDTH ? MAX_CAPTURE_WIDTH / w : 1;
    const out = document.createElement("canvas");
    out.width = Math.max(1, Math.round(w * scale));
    out.height = Math.max(1, Math.round(h * scale));
    const ctx = out.getContext("2d");
    ctx.fillStyle = "#FFFFFF";
    ctx.fillRect(0, 0, out.width, out.height);
    ctx.drawImage(src, 0, 0, out.width, out.height);
    return out.toDataURL("image/jpeg", JPEG_QUALITY);
  }

  /**
   * @param {string[]} canvasIds
   * @param {string[]} titles
   * @returns {{ title: string, dataUrl: string }[]}
   */
  function captureAll(canvasIds, titles) {
    const result = [];
    for (let i = 0; i < canvasIds.length; i++) {
      const id = canvasIds[i];
      const title = (titles && titles[i]) || id;
      const chart = charts[id];
      if (!chart) {
        continue;
      }
      try {
        const dataUrl = captureScaledDataUrl(chart);
        if (dataUrl && dataUrl.indexOf("base64,") > 0) {
          result.push({ title: title, dataUrl: dataUrl });
        }
      } catch (e) {
        console.warn("No se pudo capturar chart", id, e);
      }
    }
    return result;
  }

  function loadImage(dataUrl) {
    return new Promise(function (resolve, reject) {
      const img = new Image();
      img.onload = function () { resolve(img); };
      img.onerror = reject;
      img.src = dataUrl;
    });
  }

  async function captureMosaicAsync(canvasIds, titles) {
    const captures = captureAll(canvasIds, titles);
    if (!captures.length) {
      return null;
    }

    const imgs = [];
    for (const c of captures) {
      imgs.push({ title: c.title, img: await loadImage(c.dataUrl) });
    }

    const pad = 16;
    const titleH = 28;
    const maxW = Math.max.apply(null, imgs.map(function (x) { return x.img.width; }));
    let totalH = pad;
    imgs.forEach(function (x) {
      totalH += titleH + x.img.height + pad;
    });

    const canvas = document.createElement("canvas");
    canvas.width = maxW + pad * 2;
    canvas.height = totalH;
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = "#FFFFFF";
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = "#1F2A2E";
    ctx.font = "bold 18px Segoe UI, sans-serif";

    let y = pad;
    imgs.forEach(function (x) {
      ctx.fillText(x.title, pad, y + 18);
      y += titleH;
      const xPos = pad + Math.floor((maxW - x.img.width) / 2);
      ctx.drawImage(x.img, xPos, y);
      y += x.img.height + pad;
    });

    return canvas.toDataURL("image/jpeg", JPEG_QUALITY);
  }

  /** Descarga el mosaic en el navegador sin devolver el payload a Blazor. */
  async function downloadMosaicAsync(canvasIds, titles, fileName) {
    const dataUrl = await captureMosaicAsync(canvasIds, titles);
    if (!dataUrl) {
      return false;
    }
    window.regnepsDownload.fileFromDataUrl(dataUrl, fileName || "graficas.jpg");
    return true;
  }

  return {
    upsertChart,
    destroyChart,
    destroyAll,
    captureAll,
    captureMosaicAsync,
    downloadMosaicAsync
  };
})();

window.regnepsDownload = (function () {
  function fileFromBase64(base64, fileName, contentType) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    const blob = new Blob([bytes], { type: contentType || "application/octet-stream" });
    const url = URL.createObjectURL(blob);
    triggerDownload(url, fileName);
    URL.revokeObjectURL(url);
  }

  function fileFromDataUrl(dataUrl, fileName) {
    triggerDownload(dataUrl, fileName || "grafica.jpg");
  }

  function triggerDownload(href, fileName) {
    const a = document.createElement("a");
    a.href = href;
    a.download = fileName || "download";
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
  }

  function openUrl(url) {
    window.location.assign(url);
  }

  /** Descarga por URL sin abandonar la página Blazor (evita cancelar el circuito). */
  function downloadUrl(url) {
    const iframe = document.createElement("iframe");
    iframe.style.display = "none";
    iframe.src = url;
    document.body.appendChild(iframe);
    setTimeout(function () {
      if (iframe.parentNode) {
        iframe.parentNode.removeChild(iframe);
      }
    }, 120000);
  }

  return {
    fileFromBase64,
    fileFromDataUrl,
    openUrl,
    downloadUrl
  };
})();
