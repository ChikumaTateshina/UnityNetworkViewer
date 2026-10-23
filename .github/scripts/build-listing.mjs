// GitHub Releases に添付されたパッケージ zip から VPM listing（index.json）を生成し、
// Website/ の内容と共に _site/ へ出力する。
// 環境変数: GITHUB_REPOSITORY（owner/repo）、GITHUB_TOKEN（任意。API のレート制限回避）
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const repo = process.env.GITHUB_REPOSITORY;
if (!repo) throw new Error('GITHUB_REPOSITORY is not set');
const [owner, name] = repo.split('/');
const siteUrl = `https://${owner.toLowerCase()}.github.io/${name}`;
const config = JSON.parse(readFileSync('Website/listing.json', 'utf8'));

const headers = { Accept: 'application/vnd.github+json', 'User-Agent': 'vpm-listing-builder' };
if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;

async function fetchReleases() {
  const all = [];
  for (let page = 1; ; page++) {
    const res = await fetch(`https://api.github.com/repos/${repo}/releases?per_page=100&page=${page}`, { headers });
    if (!res.ok) throw new Error(`GitHub API ${res.status}: ${await res.text()}`);
    const items = await res.json();
    all.push(...items);
    if (items.length < 100) return all;
  }
}

const work = mkdtempSync(join(tmpdir(), 'vpm-'));
const packages = {};
for (const id of config.packages) packages[id] = { versions: {} };

for (const release of await fetchReleases()) {
  if (release.draft) continue;
  for (const asset of release.assets) {
    const id = config.packages.find((p) => asset.name.startsWith(p + '-') && asset.name.endsWith('.zip'));
    if (!id) continue;
    const res = await fetch(asset.browser_download_url, { headers: { 'User-Agent': headers['User-Agent'] } });
    if (!res.ok) throw new Error(`Download failed ${res.status}: ${asset.browser_download_url}`);
    const bytes = Buffer.from(await res.arrayBuffer());
    const file = join(work, asset.name);
    writeFileSync(file, bytes);
    const manifest = JSON.parse(execFileSync('unzip', ['-p', file, 'package.json'], { encoding: 'utf8' }));
    if (manifest.name !== id) throw new Error(`${asset.name}: package name mismatch (${manifest.name})`);
    manifest.url = asset.browser_download_url;
    manifest.zipSHA256 = createHash('sha256').update(bytes).digest('hex');
    packages[id].versions[manifest.version] = manifest;
    console.log(`listed ${id}@${manifest.version}`);
  }
}

const listing = {
  name: config.name,
  id: config.id,
  url: `${siteUrl}/index.json`,
  author: config.author,
  packages,
};

mkdirSync('_site', { recursive: true });
cpSync('Website', '_site', { recursive: true });
writeFileSync('_site/index.json', JSON.stringify(listing, null, 2) + '\n');
writeFileSync('_site/.nojekyll', '');
