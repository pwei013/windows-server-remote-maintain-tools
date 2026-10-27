#!/usr/bin/env node
'use strict';

// Real loopback HTTP against an isolated Console process and synthetic Agent servers.
// Usage: node tests/monitoring-http-smoke.cjs [--console-dll /absolute/IISManager.Console.dll]
//        [--dotnet /absolute/dotnet] [--preview] [--preview-port 5197]
// --preview keeps only this script's isolated services alive until SIGINT/SIGTERM.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const net = require('node:net');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const { randomUUID } = require('node:crypto');

const root = path.resolve(__dirname, '..');
const args = process.argv.slice(2);
function argument(name, fallback) {
  const index = args.indexOf(name);
  if (index < 0) return fallback;
  if (!args[index + 1] || args[index + 1].startsWith('--')) throw new Error('ARGUMENT_INVALID');
  return args[index + 1];
}
const dll = path.resolve(argument('--console-dll', path.join(root, 'src/IISManager.Console/bin/Release/net10.0/IISManager.Console.dll')));
const dotnet = argument('--dotnet', process.env.IIS_MONITORING_DOTNET || 'dotnet');
const preview = args.includes('--preview');
const administrationPreview = preview && args.includes('--administration-preview');
const previewPort = Number(argument('--preview-port', '0'));
safe(Number.isInteger(previewPort) && (previewPort === 0 || previewPort >= 1024 && previewPort <= 65535), 'PREVIEW_PORT_INVALID');
const agentTokens = ['A'.repeat(64), 'B'.repeat(64), 'C'.repeat(64)];
const diagnosticSentinel = 'SYNTHETIC_AGENT_DIAGNOSTIC_MUST_NOT_REACH_BROWSER';
const children = [];
const servers = [];
let temporary;
let stopping = false;
let checks = 0;
const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

function safe(condition, code) { if (!condition) throw new Error(code); }
function noSecrets(text) {
  safe(!agentTokens.some(token => text.includes(token)) && !text.includes(diagnosticSentinel), 'RESPONSE_SECRET_LEAK');
}
async function check(name, operation) {
  await operation();
  checks++;
  process.stdout.write('PASS ' + name + '\n');
}

// Native HTTP bypasses proxy environment variables. Only explicit loopback destinations are allowed.
function request(port, route, method = 'GET', body, headers = {}) {
  safe(Number.isInteger(port) && port > 0 && route.startsWith('/') && !route.startsWith('//'), 'HTTP_TARGET_INVALID');
  return new Promise((resolve, reject) => {
    const payload = body === undefined ? null : Buffer.from(JSON.stringify(body));
    const call = http.request({ hostname: '127.0.0.1', port, path: route, method, agent: false,
      headers: { ...headers, ...(payload ? { 'Content-Type': 'application/json', 'Content-Length': payload.length } : {}) } }, response => {
      const parts = []; let bytes = 0;
      response.on('data', chunk => {
        bytes += chunk.length;
        if (bytes > 2 * 1024 * 1024) call.destroy(new Error('HTTP_RESPONSE_TOO_LARGE'));
        else parts.push(chunk);
      });
      response.on('end', () => {
        const text = Buffer.concat(parts).toString('utf8');
        let json;
        if (response.headers['content-type']?.includes('application/json')) {
          try { json = JSON.parse(text); } catch { reject(new Error('HTTP_JSON_INVALID')); return; }
        }
        resolve({ status: response.statusCode, headers: response.headers, text, json });
      });
      response.on('error', reject);
    });
    call.setTimeout(2500, () => call.destroy(new Error('HTTP_TIMEOUT')));
    call.on('error', reject);
    if (payload) call.write(payload);
    call.end();
  });
}

async function listen(server) {
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  servers.push(server);
  return server.address().port;
}

async function freePort() {
  const server = net.createServer();
  const port = await listen(server);
  await new Promise(resolve => server.close(resolve));
  return port;
}

async function fakeAgent(kind, token) {
  const state = { kind, offline: kind === 'offline', validations: 0, probes: 0, requests: 0, port: 0, operationWrites: 0, operations: new Map() };
  const anchor = Date.now();
  const stamp = days => new Date(anchor + days * 86400000).toISOString();
  const thumbs = ['1', '2', '3', '4', '5', '6'].map(character => character.repeat(40));
  const certs = [30, 15, 7, -1, 60].map((days, index) => ({ thumbprint: thumbs[index], subject: 'CN=synthetic.invalid',
    friendlyName: 'Synthetic certificate ' + index, notBefore: stamp(index === 4 ? 1 : -30), notAfter: stamp(days), hasPrivateKey: true }));
  const bindings = thumbs.map((thumbprint, index) => ({ protocol: 'https', ipAddress: '*', port: 443,
    hostName: 'synthetic-' + index + '.invalid', certificateThumbprint: thumbprint, useSni: true }));
  const pool = { name: 'SyntheticPool', state: 'Started', managedRuntimeVersion: '', managedPipelineMode: 'Integrated',
    enable32BitAppOnWin64: false, autoStart: true, startMode: 'AlwaysRunning', idleTimeoutMinutes: 20,
    idleTimeoutAction: 'Terminate', identityType: 'ApplicationPoolIdentity', applications: ['SyntheticSite/'], fingerprint: 'F'.repeat(64) };
  const snapshot = { collectedAt: stamp(0), applicationPools: [pool], sites: [{ name: 'SyntheticSite', id: 1, state: 'Started',
    serverAutoStart: true, physicalPath: 'C:\\SyntheticSite', applicationPoolName: 'SyntheticPool', bindings }] };
  const topology = { collectedAt: stamp(0), applicationPools: [pool], sites: [{ id: 1, name: 'SyntheticSite', state: 'Started', bindings,
    applications: [{ path: '/', applicationPoolName: 'SyntheticPool', physicalPath: 'C:\\SyntheticSite', enabledProtocols: 'http',
      virtualDirectories: [{ path: '/', physicalPath: 'C:\\SyntheticSite' }] }] }] };
  const service = managed => ({ name: managed ? 'Synthetic.Managed' : 'Synthetic.Unmanaged', displayName: 'Synthetic service', description: '',
    state: 'Running', startType: 'Automatic', accountName: 'NT AUTHORITY\\LocalService', executablePath: 'C:\\Synthetic\\service.exe',
    processId: 100, dependencies: [], dependentServices: [], managed, canStart: false, canStop: managed,
    canRestart: managed, canUpdateStartType: managed, unavailableReason: null });
  const server = http.createServer(async (incoming, response) => {
    state.requests++;
    const send = (status, data) => { response.writeHead(status, { 'Content-Type': 'application/json' }); response.end(JSON.stringify(data)); };
    if (incoming.headers['x-iismanager-token'] !== token) { send(403, { code: 'SYNTHETIC_AUTH_REQUIRED' }); return; }
    if (state.offline) { send(503, { code: 'SYNTHETIC_OFFLINE', message: diagnosticSentinel }); return; }
    try {
      const route = incoming.url;
      if (route === '/agent/v1/info' && state.redirectTo) {
        response.writeHead(302, { Location: state.redirectTo }); response.end(); return;
      }
      if (incoming.method === 'POST') {
        let data = '';
        for await (const chunk of incoming) { data += chunk; if (data.length > 65536) throw new Error('FAKE_REQUEST_TOO_LARGE'); }
        const body = JSON.parse(data);
        if (route === '/agent/v1/operations/validate') {
          send(200, { valid: true, fingerprint: 'F'.repeat(64), affectedApplications: ['SyntheticSite/'], changes: [], warnings: [], errors: [] }); return;
        }
        if (route === '/agent/v1/operations') {
          if (body.type !== 'applicationPool.recycle' || body.target?.applicationPoolName !== 'SyntheticPool') { send(400, { code: 'SYNTHETIC_OPERATION_REJECTED' }); return; }
          state.operationWrites++;
          const record = { request: body, requestHash: '', state: 'queued', acceptedAt: new Date().toISOString(), before: pool, after: null };
          state.operations.set(body.operationId.toLowerCase(), record); send(202, record); return;
        }
        if (body.siteName !== 'SyntheticSite' || body.applicationPath !== '/' || body.url !== 'https://synthetic.invalid/health') {
          send(400, { code: 'SYNTHETIC_TARGET_REJECTED' }); return;
        }
        if (route === '/agent/v1/health-check/validate') { state.validations++; send(200, { valid: true }); return; }
        if (route === '/agent/v1/health-check') {
          state.probes++;
          send(200, { state: 'healthy', checkedAt: new Date().toISOString(), statusCode: 200, elapsedMilliseconds: 12, errorCode: null }); return;
        }
      }
      if (route.startsWith('/agent/v1/operations/')) {
        const record = state.operations.get(route.split('/').pop().toLowerCase());
        if (!record) { send(404, { code: 'SYNTHETIC_OPERATION_NOT_FOUND' }); return; }
        record.state = 'succeeded'; record.startedAt = record.acceptedAt; record.completedAt ||= new Date().toISOString(); record.after = pool;
        send(200, record); return;
      }
      const result = {
        '/agent/v1/info': { protocolVersion: '1.0', agentVersion: 'synthetic', machineName: 'Synthetic-' + kind,
          mode: 'Windows', osVersion: 'SyntheticOS', iisVersion: 'SyntheticIIS', sharedConfiguration: false, reservedPorts: [7443, 7444] },
        '/agent/v1/snapshot': snapshot,
        '/agent/v1/site-topology': topology,
        '/agent/v1/diagnostics': { collectedAt: stamp(0), mode: 'Windows', cacheSeconds: 10,
          resources: { cpuPercent: null, totalMemoryBytes: 17179869184, availableMemoryBytes: 8589934592,
            logicalProcessors: 4, disks: [{ name: 'C:\\', totalBytes: 107374182400, availableBytes: 53687091200 }] },
          processes: [], events: [], iisErrors: [], modules: ['resources', 'processes', 'events', 'iisLogs'].map(module => ({ module, state: 'available' })) },
        '/agent/v1/diagnostics/freb': { collectedAt: stamp(0), mode: 'Windows', cacheSeconds: 10, state: 'available', errorCode: null,
          sites: [{ siteId: 1, siteName: 'SyntheticSite', state: 'available', errorCode: null, truncated: false, traces: [{
            traceId: 'a'.repeat(64), startedAt: stamp(0), status: 500, subStatus: 30, win32Status: 0, timeTakenMilliseconds: 12, method: 'GET',
            failureModule: 'AspNetCoreModuleV2', failureNotification: 'EXECUTE_REQUEST_HANDLER', truncated: false,
            events: [{ sequence: 1, timeCreated: stamp(0), offsetMilliseconds: 0, eventName: 'MODULE_SET_RESPONSE_ERROR_STATUS', module: 'AspNetCoreModuleV2', notification: 'EXECUTE_REQUEST_HANDLER', status: 500, subStatus: 30, win32Status: 0 }]
          }] }] },
        '/agent/v1/certificates': certs,
        '/agent/v1/windows-services': { collectedAt: stamp(0), services: [service(true), service(false)],
          canCreate: false, creationUnavailableReason: 'Synthetic read only inventory', allowedAccounts: [] }
      }[route];
      if (kind === 'partial' && route === '/agent/v1/diagnostics/freb') { send(404, { code: 'SYNTHETIC_NOT_FOUND' }); return; }
      if (kind === 'partial' && route === '/agent/v1/certificates') { send(503, { message: diagnosticSentinel }); return; }
      if (result) send(200, result); else send(404, { code: 'SYNTHETIC_NOT_FOUND' });
    } catch { if (!response.headersSent) send(400, { code: 'SYNTHETIC_REQUEST_INVALID' }); else response.destroy(); }
  });
  state.port = await listen(server);
  process.stdout.write('RESOURCE fakeAgent=' + kind + ' pid=' + process.pid + ' port=' + state.port + ' loopback=true\n');
  return state;
}

async function prepareRuntime() {
  await fs.access(dll);
  const source = path.dirname(dll);
  const runtime = path.join(temporary, 'runtime');
  await fs.mkdir(runtime);
  for (const entry of await fs.readdir(source, { withFileTypes: true })) {
    if (entry.isFile() && (/\.dll$/i.test(entry.name) || /\.(deps|runtimeconfig)\.json$/i.test(entry.name)))
      await fs.copyFile(path.join(source, entry.name), path.join(runtime, entry.name));
    else if (entry.isDirectory() && entry.name === 'runtimes')
      await fs.cp(path.join(source, entry.name), path.join(runtime, entry.name), { recursive: true });
  }
  // Do not copy appsettings*.json or an existing NLog target from the actual checkout.
  await fs.writeFile(path.join(runtime, 'NLog.config'), '<?xml version="1.0" encoding="utf-8"?><nlog xmlns="http://www.nlog-project.org/schemas/NLog.xsd" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><targets><target xsi:type="Null" name="isolated"/></targets><rules><logger name="*" minlevel="Trace" writeTo="isolated"/></rules></nlog>');
  return path.join(runtime, path.basename(dll));
}

async function consoleProcess(runtimeDll, agents, production) {
  const label = production ? 'production' : 'development';
  const directory = path.join(temporary, label);
  await fs.mkdir(directory);
  await fs.symlink(path.join(root, 'src/IISManager.Console/wwwroot'), path.join(directory, 'wwwroot'), 'dir');
  const port = preview && !production && previewPort ? previewPort : await freePort();
  const options = { ListenAddress: '127.0.0.1', Port: port, AllowedHosts: ['127.0.0.1'], TransportMode: 'HttpToken', BrowserHttps: false,
    SourceAccessMode: 'PrivateNetwork', AllowDevelopmentHttp: !production, TaskDirectory: path.join(directory, 'tasks'), AgentTimeoutSeconds: 2,
    UserAuthentication: { Enabled: production, RequireHttps: false }, SiteAnnotations: { Enabled: false }, OperationHistory: { Enabled: false },
    Administration: { DataDirectory: path.join(temporary, 'administration-' + label), AllowDevelopmentPlaintext: administrationPreview && !production },
    Monitoring: { DataDirectory: path.join(directory, 'monitoring'), OverviewCacheSeconds: 5 },
    Servers: agents.map((agent, index) => ({ Id: agent.kind, Name: 'Synthetic ' + agent.kind, AgentUrl: 'http://127.0.0.1:' + agent.port,
      AccessToken: agentTokens[index], Simulation: false })) };
  await fs.writeFile(path.join(directory, 'appsettings.json'), JSON.stringify({ Console: options }), { mode: 0o600 });
  const environment = { PATH: process.env.PATH, ...(process.env.HOME ? { HOME: process.env.HOME } : {}),
    DOTNET_ENVIRONMENT: production ? 'Production' : 'Development', ASPNETCORE_ENVIRONMENT: production ? 'Production' : 'Development',
    DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1', HTTP_PROXY: '', HTTPS_PROXY: '', ALL_PROXY: '',
    http_proxy: '', https_proxy: '', all_proxy: '', NO_PROXY: '127.0.0.1,localhost,::1', no_proxy: '127.0.0.1,localhost,::1' };
  const child = spawn(dotnet, [runtimeDll, '--contentRoot', directory, '--webroot', path.join(directory, 'wwwroot')],
    { cwd: directory, env: environment, stdio: ['ignore', 'pipe', 'pipe'] });
  children.push(child);
  let spawnError = false;
  child.on('error', () => { spawnError = true; });
  // Runtime output is intentionally consumed without printing or saving bodies, credentials, or stack traces.
  child.stdout.on('data', () => {}); child.stderr.on('data', () => {});
  process.stdout.write('RESOURCE console=' + label + ' pid=' + child.pid + ' port=' + port + ' loopback=true\n');
  for (let attempt = 0; attempt < 80; attempt++) {
    if (spawnError || child.exitCode !== null) throw new Error('CONSOLE_STARTUP_FAILED_' + label.toUpperCase());
    try { if ((await request(port, '/api/v1/session')).status === 200) return { child, port, directory }; } catch { /* readiness only */ }
    await sleep(125);
  }
  throw new Error('CONSOLE_STARTUP_TIMEOUT_' + label.toUpperCase());
}

async function cleanup() {
  if (stopping) return;
  stopping = true;
  for (const child of children) {
    if (child.exitCode === null && child.signalCode === null) {
      const closed = once(child, 'close').catch(() => {});
      child.kill('SIGTERM');
      await Promise.race([closed, sleep(2500)]);
      if (child.exitCode === null && child.signalCode === null) { child.kill('SIGKILL'); await closed; }
    }
  }
  for (const server of servers) {
    if (server.listening) { server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve)); }
  }
  if (temporary) await fs.rm(temporary, { recursive: true, force: true });
  process.stdout.write('CLEANUP children=' + children.length + ' fakeServers=' + servers.filter(server => server instanceof http.Server).length + ' temporaryRemoved=true\n');
}

async function main() {
  const proxyNames = ['HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY', 'http_proxy', 'https_proxy', 'all_proxy', 'no_proxy'];
  process.stdout.write('NETWORK ' + proxyNames.map(name => name + '=' + (process.env[name] ? 'present' : 'absent')).join(' ') + ' nativeHttp=direct-loopback\n');
  await fs.mkdir(path.join(root, 'work'), { recursive: true });
  temporary = await fs.mkdtemp(path.join(root, 'work/monitoring-http-'));
  process.stdout.write('RESOURCE temporary=' + temporary + '\n');
  const runtimeDll = await prepareRuntime();
  const agents = [];
  for (let index = 0; index < 3; index++) agents.push(await fakeAgent(['online', 'partial', 'offline'][index], agentTokens[index]));
  const development = await consoleProcess(runtimeDll, agents, false);
  const origin = 'http://127.0.0.1:' + development.port;
  let csrf; let cookie;
  let saved; let enabled; let completedOperationId;
  const config = { serverId: 'online', siteName: 'SyntheticSite', applicationPath: '/', url: 'https://synthetic.invalid/health',
    expectedStatusCode: 200, timeoutSeconds: 5, intervalSeconds: 3600 };
  const csrfHeaders = () => ({ Origin: origin, Cookie: cookie, 'X-IISManager-CSRF': csrf, 'Sec-Fetch-Site': 'same-origin' });
  await check('GET overview=200 isolated online partial offline modules and managed services', async () => {
    const response = await request(development.port, '/api/v1/overview');
    assert.equal(response.status, 200); noSecrets(response.text);
    const byId = Object.fromEntries(response.json.servers.map(server => [server.id, server]));
    assert.equal(byId.online.connectionState, 'online');
    assert.equal(byId.partial.connectionState, 'partial'); assert.ok(byId.partial.snapshot); assert.ok(byId.partial.topology);
    assert.equal(byId.partial.certificates.length, 0); assert.equal(byId.partial.errors.length, 1);
    assert.equal(byId.partial.errors[0].module, 'certificates');
    assert.equal(byId.offline.connectionState, 'offline'); assert.equal(byId.offline.errors.length, 5);
    assert.equal(byId.offline.snapshot, null); assert.equal(byId.offline.services, null);
    assert.deepEqual(byId.online.services.services.map(service => service.name), ['Synthetic.Managed']);
    assert.deepEqual(byId.online.certificates.map(certificate => certificate.severity).sort(),
      ['notice', 'warning', 'critical', 'expired', 'notYetValid', 'missing'].sort());
  });
  await check('GET server summaries=200 excludes outbound Agent tokens', async () => {
    const response = await request(development.port, '/api/v1/servers');
    assert.equal(response.status, 200); noSecrets(response.text);
    assert.equal(response.json.length, 3); assert.ok(response.json.every(server => !('accessToken' in server)));
  });
  await check('POST without CSRF=403 and no Agent validation or probe', async () => {
    const response = await request(development.port, '/api/v1/health-checks/save', 'POST', config, { Origin: origin });
    assert.equal(response.status, 403); assert.equal(response.json.code, 'SAME_ORIGIN_REQUIRED'); noSecrets(response.text);
    assert.equal(agents[0].validations, 0); assert.equal(agents[0].probes, 0);
  });
  await check('lowercase post cannot bypass same-origin and CSRF checks', async () => {
    const response = await request(development.port, '/api/v1/health-checks/save', 'post', config, { Origin: origin });
    assert.ok([400, 403, 405].includes(response.status)); assert.equal(agents[0].validations, 0); assert.equal(agents[0].probes, 0);
  });
  await check('GET session=200 obtains opaque CSRF state without enabling authentication bypass in Production', async () => {
    const response = await request(development.port, '/api/v1/session');
    assert.equal(response.status, 200); assert.equal(response.json.authenticationRequired, false);
    csrf = response.json.csrfToken;
    assert.match(csrf, /^[A-F0-9]{64}$/);
    cookie = response.headers['set-cookie'].map(value => value.split(';', 1)[0]).join('; ');
    assert.ok(cookie.includes('IISManager.Csrf='));
  });
  await check('progress overview completes through read-only polling and one-server refresh', async () => {
    let result;
    for (let attempt = 0; attempt < 40; attempt++) {
      result = await request(development.port, '/api/v1/overview/progress');
      assert.equal(result.status, 200); noSecrets(result.text);
      if (!result.json.isRefreshing) break;
      await sleep(50);
    }
    assert.equal(result.json.completedServers, 3); assert.equal(result.json.totalServers, 3);
    const retried = await request(development.port, '/api/v1/servers/online/overview/refresh');
    assert.equal(retried.status, 200); assert.equal(retried.json.id, 'online');
  });
  await check('diagnostics and permissions return safe server-scoped values', async () => {
    const diagnostics = await request(development.port, '/api/v1/servers/online/diagnostics');
    assert.equal(diagnostics.status, 200); assert.equal(diagnostics.json.resources.cpuPercent, null); noSecrets(diagnostics.text);
    const permissions = await request(development.port, '/api/v1/permissions');
    assert.equal(permissions.status, 200); assert.equal(permissions.json.administration, administrationPreview);
    assert.equal(permissions.json.servers.length, 3);
    const denied = await request(development.port, '/API/v1/Administration/servers');
    assert.equal(denied.status, administrationPreview ? 200 : 403);
    if (!administrationPreview) assert.equal(denied.json.code, 'ADMINISTRATION_DENIED');
  });
  await check('request-level FREB diagnostics proxy preserves safe pipeline metadata and old Agent compatibility', async () => {
    const response = await request(development.port, '/api/v1/servers/online/diagnostics/freb');
    assert.equal(response.status, 200); noSecrets(response.text);
    assert.equal(response.json.sites[0].traces[0].status, 500);
    assert.equal(response.json.sites[0].traces[0].events[0].module, 'AspNetCoreModuleV2');
    const unsupported = await request(development.port, '/api/v1/servers/partial/diagnostics/freb');
    assert.equal(unsupported.status, 501); assert.equal(unsupported.json.code, 'FREB_UNSUPPORTED'); noSecrets(unsupported.text);
  });
  if (administrationPreview) await check('connection test refuses redirects without forwarding saved credentials', async () => {
    const list = await request(development.port, '/api/v1/administration/servers');
    assert.equal(list.status, 200); noSecrets(list.text);
    const otherCalls = agents[1].requests;
    agents[0].redirectTo = 'http://127.0.0.1:' + agents[1].port + '/agent/v1/info';
    try {
      const result = await request(development.port, '/api/v1/administration/servers/test', 'POST', {
        revision: list.json.revision, id: 'online', name: 'Synthetic online',
        agentUrl: 'http://127.0.0.1:' + agents[0].port, accessToken: agentTokens[0],
        group: '', environment: '', serverCertificateThumbprint: ''
      }, csrfHeaders());
      assert.ok(result.status >= 400); noSecrets(result.text); assert.equal(agents[1].requests, otherCalls);
    } finally { agents[0].redirectTo = null; }
  });
  await check('baseline explicit save survives independent readback and stale revisions are rejected', async () => {
    const before = await request(development.port, '/api/v1/servers/online/configuration-baseline');
    assert.equal(before.status, 200); assert.equal(before.json.baseline, null);
    const body = { label: 'Synthetic baseline', revision: null, currentFingerprint: before.json.currentFingerprint };
    const savedBaseline = await request(development.port, '/api/v1/servers/online/configuration-baseline', 'POST', body, csrfHeaders());
    assert.equal(savedBaseline.status, 200); noSecrets(savedBaseline.text);
    const readback = await request(development.port, '/api/v1/servers/online/configuration-baseline');
    assert.equal(readback.json.matches, true); assert.equal(readback.json.baseline.revision, savedBaseline.json.revision);
    const stale = await request(development.port, '/api/v1/servers/online/configuration-baseline', 'POST', body, csrfHeaders());
    assert.equal(stale.status, 409); assert.equal(stale.json.code, 'BASELINE_REVISION_CONFLICT');
  });
  await check('history CSV export and all new page entries are served without Agent credentials', async () => {
    const exported = await request(development.port, '/api/v1/operations/export');
    assert.equal(exported.status, 200); assert.match(exported.headers['content-type'], /text\/csv/); noSecrets(exported.text);
    for (const page of ['maintenance', 'diagnostics', 'baselines', 'servers']) {
      const response = await request(development.port, '/' + page + '.html');
      assert.equal(response.status, 200); assert.match(response.text, /main-nav/); noSecrets(response.text);
    }
  });
  await check('POST save=200 validates only and persists a default-disabled check', async () => {
    const response = await request(development.port, '/api/v1/health-checks/save', 'POST', config, csrfHeaders());
    assert.equal(response.status, 200); noSecrets(response.text); saved = response.json;
    assert.equal(saved.enabled, false); assert.equal(saved.result, null); assert.equal(saved.lastSuccessAt, null);
    assert.equal(agents[0].validations, 1); assert.equal(agents[0].probes, 0);
    const persisted = JSON.parse(await fs.readFile(path.join(development.directory, 'monitoring/health-checks.json'), 'utf8'));
    assert.equal(persisted.checks[0].id, saved.id); assert.equal(persisted.checks[0].enabled, false);
  });
  await check('POST disabled run=409 without a business probe', async () => {
    const response = await request(development.port, '/api/v1/health-checks/' + saved.id + '/run', 'POST', {}, csrfHeaders());
    assert.equal(response.status, 409); assert.equal(response.json.code, 'HEALTH_CHECK_DISABLED'); assert.equal(agents[0].probes, 0);
  });
  await check('POST enable and run=200 persists actual HTTP result and last success', async () => {
    const edit = await request(development.port, '/api/v1/health-checks/save', 'POST', { ...config, id: saved.id, revision: saved.revision, enabled: true }, csrfHeaders());
    assert.equal(edit.status, 200); enabled = edit.json;
    const run = await request(development.port, '/api/v1/health-checks/' + saved.id + '/run', 'POST', {}, csrfHeaders());
    assert.equal(run.status, 200); noSecrets(run.text);
    assert.equal(run.json.result.state, 'healthy'); assert.equal(run.json.result.statusCode, 200);
    assert.equal(run.json.lastSuccessAt, run.json.result.checkedAt); assert.ok(agents[0].probes >= 1);
    const persisted = JSON.parse(await fs.readFile(path.join(development.directory, 'monitoring/health-checks.json'), 'utf8'));
    assert.equal(persisted.checks[0].result.state, 'healthy'); assert.equal(persisted.checks[0].lastSuccessAt, run.json.lastSuccessAt);
    const readback = await request(development.port, '/api/v1/health-checks');
    assert.equal(readback.status, 200); assert.equal(readback.json[0].lastSuccessAt, run.json.lastSuccessAt);
  });
  await check('POST stale revision=409 preserves the enabled configuration', async () => {
    const response = await request(development.port, '/api/v1/health-checks/save', 'POST', { ...config, id: saved.id, revision: saved.revision, enabled: false }, csrfHeaders());
    assert.equal(response.status, 409); assert.equal(response.json.code, 'HEALTH_CONFIG_CONFLICT');
    const current = await request(development.port, '/api/v1/health-checks');
    assert.equal(current.json[0].revision, enabled.revision); assert.equal(current.json[0].enabled, true);
  });
  await check('new IIS operation gets one durable background health follow-up without browser polling or mutation replay', async () => {
    const operationId = randomUUID(); const beforeProbes = agents[0].probes, beforeWrites = agents[0].operationWrites;
    const operation = { operationId, type: 'applicationPool.recycle', target: { applicationPoolName: 'SyntheticPool' }, parameters: {}, expectedFingerprint: 'F'.repeat(64), expiresAt: new Date(Date.now() + 300000).toISOString() };
    const submitted = await request(development.port, '/api/v1/servers/online/operations', 'POST', operation, csrfHeaders());
    assert.equal(submitted.status, 202); assert.equal(submitted.json.state, 'queued'); assert.equal(submitted.json.healthFollowUp.state, 'pending'); noSecrets(submitted.text);
    const file = path.join(development.directory, 'tasks', operationId + '.json'); let persisted;
    // Read the synthetic local ledger only: an open browser or GET operation handler must not be needed to start follow-up.
    for (let attempt = 0; attempt < 120; attempt++) {
      persisted = JSON.parse(await fs.readFile(file, 'utf8'));
      if (!['pending', 'running'].includes(persisted.healthFollowUp?.state)) break;
      await sleep(100);
    }
    assert.equal(persisted.state, 'succeeded'); assert.equal(persisted.healthFollowUp.state, 'completed');
    assert.equal(persisted.healthFollowUp.results.length, 1); assert.equal(persisted.healthFollowUp.results[0].state, 'healthy');
    assert.equal(agents[0].probes, beforeProbes + 1); assert.equal(agents[0].operationWrites, beforeWrites + 1); assert.equal(agents[1].probes, 0);
    assert.ok(!JSON.stringify(persisted.healthFollowUp).includes('https://synthetic.invalid'));
    const duplicate = await request(development.port, '/api/v1/servers/online/operations', 'POST', operation, csrfHeaders()); assert.equal(duplicate.status, 202);
    const readback = await request(development.port, '/api/v1/servers/online/operations/' + operationId);
    assert.equal(readback.status, 200); assert.equal(readback.json.healthFollowUp.state, 'completed');
    assert.equal(agents[0].probes, beforeProbes + 1); assert.equal(agents[0].operationWrites, beforeWrites + 1);
    completedOperationId = operationId;
  });
  await check('POST disable while Agent offline=200 skips validation and persists disabled state', async () => {
    const validations = agents[0].validations; const probes = agents[0].probes;
    agents[0].offline = true;
    const response = await request(development.port, '/api/v1/health-checks/save', 'POST', { ...config, id: saved.id, revision: enabled.revision, enabled: false }, csrfHeaders());
    assert.equal(response.status, 200); saved = response.json;
    assert.equal(saved.enabled, false); assert.equal(agents[0].validations, validations); assert.equal(agents[0].probes, probes);
    const readback = await request(development.port, '/api/v1/health-checks');
    assert.equal(readback.json[0].enabled, false);
    const persisted = JSON.parse(await fs.readFile(path.join(development.directory, 'monitoring/health-checks.json'), 'utf8'));
    assert.equal(persisted.checks[0].enabled, false);
    agents[0].offline = false;
  });
  await check('POST delete=200 followed by independent empty API and disk readbacks', async () => {
    const response = await request(development.port, '/api/v1/health-checks/' + saved.id + '/delete', 'POST', { revision: saved.revision }, csrfHeaders());
    assert.equal(response.status, 200); assert.equal(response.json.deleted, true);
    const readback = await request(development.port, '/api/v1/health-checks');
    assert.equal(readback.status, 200); assert.deepEqual(readback.json, []);
    const persisted = JSON.parse(await fs.readFile(path.join(development.directory, 'monitoring/health-checks.json'), 'utf8'));
    assert.deepEqual(persisted.checks, []);
  });
  const production = await consoleProcess(runtimeDll, agents, true);
  await check('Production GET overview unauthenticated=401 without any outbound Agent request', async () => {
    const before = agents.reduce((sum, agent) => sum + agent.requests, 0);
    const response = await request(production.port, '/api/v1/overview');
    assert.equal(response.status, 401); assert.equal(response.json.code, 'APP_USER_SESSION_REQUIRED'); noSecrets(response.text);
    assert.equal(agents.reduce((sum, agent) => sum + agent.requests, 0), before);
  });
  await check('Production GET overview.html unauthenticated=302 to login', async () => {
    const response = await request(production.port, '/overview.html');
    assert.equal(response.status, 302); assert.match(response.headers.location, /^\/login\.html\?returnUrl=/); noSecrets(response.text);
  });
  process.stdout.write('SUMMARY passed=' + checks + ' environment=isolated-loopback syntheticAgents=true actualConsoleHttp=true timestamp=' + new Date().toISOString() + '\n');
  if (preview && !args.includes('--no-wait')) {
    // Populate both UI states after destructive smoke assertions have completed.
    // All writes still target the isolated temporary Console and synthetic Agents.
    const disabled = await request(development.port, '/api/v1/health-checks/save', 'POST', { ...config, serverId: 'partial' }, csrfHeaders());
    safe(disabled.status === 200 && disabled.json.enabled === false, 'PREVIEW_DISABLED_CHECK_FAILED');
    const healthy = await request(development.port, '/api/v1/health-checks/save', 'POST', { ...config, enabled: true }, csrfHeaders());
    safe(healthy.status === 200, 'PREVIEW_HEALTHY_CHECK_FAILED');
    const observed = await request(development.port, '/api/v1/health-checks/' + healthy.json.id + '/run', 'POST', {}, csrfHeaders());
    safe(observed.status === 200 && observed.json.result.state === 'healthy', 'PREVIEW_HEALTHY_RUN_FAILED');
    process.stdout.write('PREVIEW url=' + origin + '/overview.html checks=2 healthy=1 disabled=1 certWarnings=true stop=SIGINT cleanup=on-exit\n');
    if (completedOperationId) process.stdout.write('PREVIEW operationUrl=' + origin + '/?server=online&operation=' + completedOperationId + '\n');
    await new Promise(resolve => { process.once('SIGINT', resolve); process.once('SIGTERM', resolve); });
  }
}

for (const [signal, exitCode] of [['SIGINT', 130], ['SIGTERM', 143]]) {
  process.once(signal, () => { cleanup().finally(() => process.exit(exitCode)); });
}

main().catch(error => {
  // Never print assertions or raw exception text: those can contain request bodies, cookies or credentials.
  process.stderr.write('FAIL monitoring-http-smoke type=' + (error?.constructor?.name || 'Error') +
    ' code=' + (/^[A-Z][A-Z0-9_]+$/.test(error?.message || '') ? error.message : 'ASSERTION_OR_RUNTIME_FAILURE') + '\n');
  process.exitCode = 1;
}).finally(cleanup);
