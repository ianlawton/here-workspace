![HERE Core UI Example Application](../../assets/HERO-STARTER-HERE-CORE-UI.png)

> **_:information_source: HERE Core UI:_** [HERE Core UI](https://resources.here.io/docs/core/hc-ui/) is a commercial product and this repo is for evaluation purposes (See [LICENSE.MD](LICENSE.MD)). Use of the HERE Core Container and HERE Core UI components is only granted pursuant to a license from HERE (see [manifest](public/manifest.fin.json)). Please [**contact us**](https://www.here.io/contact) if you would like to request a developer evaluation key or to discuss a production license.

# HERE Core UI Platform Basic

Using a simplified approach to populating a platform this basic example will use the list of application defined in your manifest.fin.json to populate all the Workspace components Home, Store, Dock and Notifications.

This example assumes you have already [set up your development environment](https://resources.here.io/docs/core/develop/)

## Running the Sample

To run this sample you can:

- Clone this repo and follow the instructions below. This will let you customize the sample to learn more about our APIs.
- Launch the Github hosted version of this sample to interact with it by going to the following link: [Github Workspace Starter - HERE Core UI Platform Starter Basic](https://start.openfin.co/?manifest=https%3A%2F%2Fbuilt-on-openfin.github.io%2Fworkspace-starter%2Fworkspace%2Fv23.0.0%2Fworkspace-platform-starter-basic%2Fmanifest.fin.json)

## Getting Started

1. Install dependencies and do an initial build. Note that these examples assume you are in the sub-directory for the example.

```shell
npm run setup
```

2. Optional (if you wish to pin the version of HERE Core UI to version 23.0.0 and you are on Windows) - Set Windows registry key for [Desktop Owner Settings](https://resources.here.io/docs/core/manage/desktops/dos/).
   This example runs a utility [dos.mjs](./scripts/dos.mjs) that adds the Windows registry key for you, pointing to a local desktop owner
   settings file so you can test these settings. If you already have a desktop owner settings file, this script prompts to overwrite the location. Be sure to capture the existing location so you can update the key when you are done using this example.

   (**WARNING**: This script kills all open HERE processes. **This is not something you should do in production to close apps as force killing processes could kill an application while it's trying to save state/perform an action**).

```shell
npm run dos
```

3. Start the test server in a new window.

```shell
npm run start
```

4. Start Your HERE Core UI Platform (this starts Workspace if it isn't already running).

```shell
npm run client
```

5. Type any character into the search box to show the default list of Applications sourced from the manifest.

6. Build the project if you have changed the code.

```shell
npm run build
```

![HERE Core UI Platform Starter Basic](workspace-platform-starter-basic.gif)

### Note About The App

This is a headless application. If you wish to debug it then you can update the [manifest file](public/manifest.fin.json) and set platform.autoShow to **true**. Otherwise you can use Process Manager (which is included in your list of apps).

## How it works

The Server in this example provides two sets of content over HTTP GET.

- [A Desktop Owner Settings file to pin the version of HERE Core UI (Optional)](./public/common/dos.json)
- Examples of View and Snapshot Manifest Types

### How this example works

For a more detailed look at how each component is used please see the individual examples.

- [Register with Home](../register-with-home/)
- [Register with Store](../register-with-store/)
- [Register with Dock](../register-with-dock/)
- [Register with Notifications](../register-with-notifications/)

### Note About This Example

This is an example of how to use our APIs to configure HERE Core UI. It's purpose is to provide an example and provide suggestions. This is not a production application and shouldn't be treated as such. Please use this as a guide and provide feedback. Thanks!

---

## Bloomberg Live TV App

The Bloomberg Live TV app streams live Bloomberg Television directly inside a HERE Core UI window. It uses a local transcoding proxy to convert Bloomberg's H.264/AAC HLS stream to VP8/Vorbis WebM, which is required because OpenFin's Chromium runtime does not ship with proprietary H.264 codec support.

Two server implementations are provided — use whichever suits your stack:

| | Node.js | .NET Core (C#) |
|---|---|---|
| Entry point | `scripts/serve.mjs` | `bloomberg-proxy/` |
| Runtime required | Node.js 20+ + Gyan.FFmpeg | .NET 9 SDK only |
| External ffmpeg binary | Required | **Not required** (bundled via NuGet) |

---

### Option 1 — Node.js Server

**Requirements:**
- Node.js 20+: `winget install OpenJS.NodeJS.LTS`
- ffmpeg with libvpx/libvorbis: `winget install Gyan.FFmpeg`

Update the `FFMPEG` path constant in `scripts/serve.mjs` to match your ffmpeg install:

```js
const FFMPEG = 'C:\\path\\to\\ffmpeg.exe';
```

**Terminal 1 — HTTP server + transcoding proxy:**

```shell
node scripts/serve.mjs
```

**Terminal 2 — HERE runtime:**

```shell
node scripts/launch.mjs
```

---

### Option 2 — .NET Core Server (recommended)

**Requirements:**
- .NET 9 SDK: `winget install Microsoft.DotNet.SDK.9`
- No external ffmpeg install needed — native FFmpeg libraries are bundled automatically via the `Sdcb.FFmpeg.runtime.windows-x64` NuGet package.

**Build:**

```shell
cd bloomberg-proxy
dotnet build
```

**Terminal 1 — HTTP server + transcoding proxy:**

```shell
cd bloomberg-proxy
dotnet run
```

**Terminal 2 — HERE runtime:**

```shell
node scripts/launch.mjs
```

---

### Using Bloomberg Live TV

1. With both terminals running, the HERE Dock will appear on screen.
2. Press `Ctrl+Space` to open Home (search).
3. Type **Bloomberg** and press Enter, or find it in the Store.
4. The app opens in a HERE browser window — allow ~10 seconds for the stream to buffer and start playing.
5. The window can be docked, tiled, or added to any workspace layout.

### How the Transcoding Proxy Works

Bloomberg's live stream (`phoenix-us.m3u8`) is standard H.264/AAC in HLS format. OpenFin's Chromium runtime cannot decode H.264 without a proprietary codec library. Both proxy implementations solve this by:

1. Opening the Bloomberg HLS stream directly
2. Decoding H.264 video and AAC audio
3. Re-encoding in real time to VP8/Vorbis WebM (natively supported by open-source Chromium)
4. Streaming the output to the browser via a chunked HTTP response at `/bloomberg-stream`

The .NET version performs all transcoding in-process using the Sdcb.FFmpeg managed bindings — no external process is spawned.
