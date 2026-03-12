import { createServer } from 'http';
import { createReadStream, statSync, existsSync } from 'fs';
import { join, extname } from 'path';
import { fileURLToPath } from 'url';
import { dirname } from 'path';
import { spawn } from 'child_process';

const __dirname = dirname(fileURLToPath(import.meta.url));
const PUBLIC_DIR = join(__dirname, '..', 'public');
const PORT = 8080;

const FFMPEG = 'C:\\Users\\Ian\\AppData\\Local\\Microsoft\\WinGet\\Packages\\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\\ffmpeg-8.0.1-full_build\\bin\\ffmpeg.exe';

const BLOOMBERG_STREAM = 'https://www.bloomberg.com/media-manifest/streams/phoenix-us.m3u8';

const MIME = {
  '.html': 'text/html',
  '.js':   'application/javascript',
  '.json': 'application/json',
  '.css':  'text/css',
  '.png':  'image/png',
  '.ico':  'image/x-icon',
  '.svg':  'image/svg+xml',
};

createServer((req, res) => {
  const url = req.url.split('?')[0];

  // Transcoding proxy endpoint — streams Bloomberg as VP8/Vorbis WebM
  if (url === '/bloomberg-stream') {
    console.log('[proxy] Starting Bloomberg transcoded stream');
    res.setHeader('Content-Type', 'video/webm');
    res.setHeader('Cache-Control', 'no-cache');
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.setHeader('Transfer-Encoding', 'chunked');

    const ff = spawn(FFMPEG, [
      '-re',
      '-i', BLOOMBERG_STREAM,
      '-vcodec', 'libvpx',
      '-b:v', '1500k',
      '-crf', '10',
      '-acodec', 'libvorbis',
      '-b:a', '128k',
      '-f', 'webm',
      '-deadline', 'realtime',
      '-cpu-used', '8',
      'pipe:1'
    ], { stdio: ['ignore', 'pipe', 'pipe'] });

    ff.stdout.pipe(res);

    ff.stderr.on('data', d => process.stdout.write('[ffmpeg] ' + d));

    req.on('close', () => { ff.kill(); console.log('[proxy] Client disconnected, ffmpeg killed'); });
    ff.on('exit', code => { console.log('[proxy] ffmpeg exited', code); res.end(); });
    return;
  }

  // Static file server
  let filePath = join(PUBLIC_DIR, url);
  if (!existsSync(filePath) || statSync(filePath).isDirectory()) {
    filePath = join(filePath, 'index.html');
  }
  if (!existsSync(filePath)) {
    res.writeHead(404); res.end('Not found'); return;
  }
  const ext = extname(filePath);
  res.setHeader('Cache-Control', 'no-cache');
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.writeHead(200, { 'Content-Type': MIME[ext] || 'application/octet-stream' });
  createReadStream(filePath).pipe(res);

}).listen(PORT, () => console.log(`Serving on http://localhost:${PORT}`));
