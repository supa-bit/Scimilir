# Scimilir

Chat with AI models from your PC's browser, or from your phone on the same Wi-Fi.

- **Models on your PC**: Scimilir runs model files itself (using the
  [LLamaSharp](https://github.com/SciSharp/LLamaSharp) library, which is built
  on llama.cpp). Nothing else to install, and your chats never leave your PC.
  It uses your graphics card when it can.
- **Models in the cloud**: with an [OpenRouter](https://openrouter.ai) key you
  get hundreds of cloud models (Claude, GPT, Gemini, Llama and more) in the
  same chat page.
- **One set of chats** shared by your PC and phone, saved on the PC.
- **A password**, so only you can use it.

## Install (Windows)

1. Go to the **Releases** page: <https://github.com/supa-bit/scimilir/releases>
   (you need to be logged in to GitHub, as the project is private).
2. Under the newest version, press **Scimilir-Setup-….exe** to download it.
3. Open the downloaded file. If Windows says it **protected your PC**, press
   **More info**, then **Run anyway**. (It says this about any program that
   isn't signed by a known company.)
4. Tick any extras you want (a desktop icon, or starting with Windows so your
   phone can always reach it) and press **Install**. It doesn't need
   administrator rights or anything else installed.
5. Scimilir opens in your browser. **Choose a password.**
6. Windows asks whether to let Scimilir communicate on networks: tick
   **Private networks** and press **Allow**. (This is what lets your phone
   connect.)

**Using it after that:** open **Scimilir** from the Start menu. While it runs,
its icon sits by the clock (press the small **^** arrow if you can't see
it). **Click** the icon to open Scimilir in your browser; **right-click** it
and choose **Quit Scimilir** to stop it.

## Get a model for your PC

Open **Settings → On this PC**. Press one of the suggested models (or search),
then press **Download** next to a file. Files marked **Q4_K_M** (shown as
"good start") are a good balance of quality and size.

How big a model can I run? It mostly depends on your graphics card's memory
(VRAM). To find it: open *Task Manager → Performance → GPU* and look at
"Dedicated GPU memory".

| Graphics card memory | Try |
|---|---|
| 4 GB or less | Llama 3.2 3B or Qwen3 4B (about 2–3 GB files) |
| 6–8 GB | Qwen3 8B (about 5 GB) |
| 12 GB or more | Bigger models, around 14B (about 9 GB files) |

A model too big for the card still works, but runs on the processor, which is
much slower. When the download finishes, pick the model at the top of the
chat page and say hello. The first reply takes a bit longer while it loads.

## Use it on your phone

1. Connect the phone to the same Wi-Fi as the PC.
2. On the PC, open **Settings → Phone** and scan the QR code with the phone's
   camera (or type the address shown, like `http://192.168.1.20:5080`).
3. Log in with your password.
4. Optional: in the phone browser's menu choose **Add to Home screen**, so
   Scimilir opens like an app.

The PC must be on, with Scimilir running, for the phone to use it.

If the phone can't connect: in Windows, search the Start menu for "Allow an
app through Windows Firewall", press *Change settings*, and tick **Private**
next to "Scimilir". Also make sure your Wi-Fi is set as a
*Private* network (*Settings → Network & internet → Wi-Fi → your network*).

## Cloud models (OpenRouter)

1. Make an account at <https://openrouter.ai> and add some credit (a few
   models are free).
2. At <https://openrouter.ai/keys> press **Create Key** and copy it.
3. In Scimilir, open **Settings → Cloud**, paste the key and press Save.

The cloud models then appear in the model list. With these, your messages go
over the internet to the company running the model, and most cost a little per
message. OpenRouter's website shows what you've spent.

## Where your things are kept

Everything you make is kept in your own app-data folder, separate from the
program, so updating or removing Scimilir never touches it. To open it, press
**Windows key + R**, type `%LOCALAPPDATA%\Scimilir` and press Enter. Inside:

- `settings.json`: your settings (the password is saved only as a scrambled
  "hash", never as itself; the OpenRouter key is saved as is, so keep this
  folder private)
- `chats/`: one file per chat
- `models/`: downloaded model files (these are big: delete them in
  *Settings → On this PC* when you don't need them)
- `scimilir.log`: what Scimilir did last time it ran, handy if something
  goes wrong

To back up your chats, copy the `chats` folder somewhere safe.

## Updating

Download the newest installer from the Releases page and run it. It replaces
the old version and keeps your chats, settings and models.

## Removing Scimilir

Open Windows **Settings → Apps → Installed apps**, find **Scimilir**, press
**…** and **Uninstall**. Your chats and models stay in
`%LOCALAPPDATA%\Scimilir`; delete that folder too if you want them gone.

## Good to know

- Scimilir uses plain `http://` on your home network, which is fine at home
  but not on public Wi-Fi. Using it away from home safely needs extra setup
  (for example [Tailscale](https://tailscale.com)); ask if you'd like that.
- If you lock your phone while a reply is being written, the reply may stop
  where it got to. What was written is kept.
- One reply is written at a time on the PC; a second one waits its turn.

## For developers

- Run from the source code: install the .NET 10 SDK, then double-click
  `start.bat` (or run `dotnet run --project Scimilir`). It uses the same data
  folder as the installed app, so quit one before starting the other.
- The installer: `.github/workflows/installer.yml` builds it on GitHub for
  every change to `main` (find it under the **Actions** tab → the run →
  **Artifacts**). To put a new version on the Releases page, raise
  `<Version>` in `Scimilir/Scimilir.csproj` and push a tag like `v0.3.0`.
  The installer itself is made by [Inno Setup](https://jrsoftware.org/isinfo.php)
  from `installer/Scimilir.iss`.
- `Scimilir/`: the program. `Program.cs` starts it, `Api.cs` holds the web
  addresses the page uses, `Providers/` talks to models (on this PC through
  LLamaSharp, in the cloud through OpenRouter, and downloads from Hugging
  Face), `Storage/` saves settings and chats, and `wwwroot/` is the chat page.
- `Scimilir.Tests/`: automatic tests. Run them with `dotnet test`. GitHub
  runs them too, on every push.
- `tools/make_tiny_model.py` made `Scimilir.Tests/Assets/tiny-chat.gguf`, a
  120 KB model with random weights that the tests use. It talks gibberish, but
  it checks the whole path from message to reply.
