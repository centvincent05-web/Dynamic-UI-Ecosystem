// Mengirim progress download ke aplikasi Dynamic Island (hanya ke laptop ini sendiri: 127.0.0.1).
const URL = "http://127.0.0.1:48321/downloads";
let timer = null;
let done = [];

async function send() {
  const items = await chrome.downloads.search({ state: "in_progress" });
  const body = {
    items: items.filter(d => !d.paused).map(d => ({
      name: d.filename, received: d.bytesReceived, total: d.totalBytes > 0 ? d.totalBytes : d.fileSize
    })),
    done
  };
  done = [];
  try {
    await fetch(URL, { method: "POST", headers: { "Content-Type": "application/json", "X-Island": "1" }, body: JSON.stringify(body) });
  } catch (e) { /* aplikasi tidak jalan: abaikan */ }
  if (items.length === 0 && timer) { clearInterval(timer); timer = null; }
}

function start() {
  if (!timer) timer = setInterval(send, 500);
  send();
}

chrome.downloads.onCreated.addListener(start);
chrome.downloads.onChanged.addListener(async delta => {
  if (delta.state && delta.state.current === "complete") {
    const [d] = await chrome.downloads.search({ id: delta.id });
    if (d) done.push(d.filename);
  }
  start();
});
start();
