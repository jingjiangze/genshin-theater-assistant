// box MCP 客户端（最小实现）：initialize → tools/list → tools/call
// 凭据只从环境变量 BOX_TOKEN 读，绝不写入文件或日志。
import fs from 'node:fs';

const ENDPOINT = process.env.BOX_MCP_URL || 'https://mcp.box.simular.ai/mcp';
const TOKEN = process.env.BOX_TOKEN || '';
if (!TOKEN) { console.error('缺 BOX_TOKEN'); process.exit(2); }

let sessionId = '';
let rpcId = 0;

async function rpc(method, params, { notify = false } = {}) {
  const body = notify
    ? { jsonrpc: '2.0', method, params }
    : { jsonrpc: '2.0', id: ++rpcId, method, params };
  const headers = {
    'Content-Type': 'application/json',
    'Accept': 'application/json, text/event-stream',
    'Authorization': `Bearer ${TOKEN}`,
    'MCP-Protocol-Version': '2025-06-18',
  };
  if (sessionId) headers['Mcp-Session-Id'] = sessionId;

  let res;
  for (let attempt = 1; ; attempt++) {
    try { res = await fetch(ENDPOINT, { method: "POST", headers, body: JSON.stringify(body) }); break; }
    catch (e) {
      if (attempt >= 8) throw e;
      await new Promise(r => setTimeout(r, 1500 * attempt));
    }
  }
  const sid = res.headers.get('mcp-session-id');
  if (sid) sessionId = sid;
  const text = await res.text();
  if (!res.ok) throw new Error(`HTTP ${res.status}: ${text.slice(0, 300)}`);

  // Streamable HTTP 可能返回 SSE
  let payload = text;
  if (text.startsWith('event:') || text.includes('\ndata:') || text.startsWith('data:')) {
    const lines = text.split(/\r?\n/).filter(l => l.startsWith('data:'));
    payload = lines.map(l => l.slice(5).trim()).filter(Boolean).join('\n');
  }
  try { return JSON.parse(payload); } catch { return { raw: payload.slice(0, 400) }; }
}

const cmd = process.argv[2] || 'list';
const arg = process.argv[3] || '';

if (cmd === 'list') {
  const init = await rpc('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: { roots: {} },
    clientInfo: { name: 'theater-assistant-audit', version: '0.1.0' },
  });
  console.log('session =', sessionId ? sessionId.slice(0, 8) + '…' : '(none)');
  console.log('server  =', JSON.stringify(init.result?.serverInfo ?? init).slice(0, 300));
  await rpc('notifications/initialized', {}, { notify: true });
  const tools = await rpc('tools/list', {});
  const list = tools.result?.tools ?? [];
  console.log('tools =', list.length);
  const out = [];
  for (const t of list) {
    const props = t.inputSchema?.properties ?? {};
    out.push({ name: t.name, desc: (t.description || '').slice(0, 90), params: Object.keys(props) });
    console.log(`  - ${t.name}(${Object.keys(props).join(', ')}) :: ${(t.description || '').slice(0, 80)}`);
  }
  fs.writeFileSync('_lead-verify/box-tools.json', JSON.stringify(out, null, 1));
  process.exit(0);
}

if (cmd === 'call') {
  const tool = arg;
  let rawArgs = process.argv[4] || '{}';
  if (rawArgs.startsWith("@")) rawArgs = fs.readFileSync(rawArgs.slice(1), "utf8");
  const args = JSON.parse(rawArgs);
  await rpc('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: { roots: {} },
    clientInfo: { name: 'theater-assistant-audit', version: '0.1.0' },
  });
  await rpc('notifications/initialized', {}, { notify: true });
  const res = await rpc('tools/call', { name: tool, arguments: args });
  const content = res.result?.content ?? res.result ?? res;
  const text = Array.isArray(content)
    ? content.map(c => (c.type === 'text' ? c.text : `[${c.type}]`)).join('\n')
    : JSON.stringify(content);
  console.log(text.slice(0, 4000));
  // 不要直接 process.exit：Windows 上 libuv 若还有未关闭 handle 会触发
  // "Assertion failed: !(handle->flags & UV_HANDLE_CLOSING)" 并以 0xC0000409 退出，
  // 让调用方（execFileSync）把**成功**的调用误判为失败。
  // 改为设置 exitCode 并让事件循环自然结束。
  process.exitCode = res.result?.isError ? 1 : 0;
  // 等 undici 把 keep-alive 连接关掉再退出：否则 Windows libuv 会断言崩溃（0xC0000409），
  // 让调用方把成功误判为失败。150ms 足够浏览器/HTTP 栈释放句柄。
  await new Promise(r => setTimeout(r, 150));
  process.exit(process.exitCode);
}
console.error('用法: node box-mcp.mjs list | call <tool> <json-args>');
process.exit(2);
