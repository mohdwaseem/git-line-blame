# Git Line Blame

Shows inline git blame information — **author, commit hash, date, and message** — as faded italic text at the end of the line where your cursor currently sits, just like GitLens for VS Code.

![Git Line Blame in action](https://raw.githubusercontent.com/mwaseem/GitLens/main/preview.png)

## Features

- **Inline blame** — see who last changed the current line without leaving the editor
- **Rich info** — displays author name, short commit hash, relative date, and commit message
- **Zero friction** — appears instantly when you move the cursor; disappears when you leave the line
- **Unsaved changes** — lines with uncommitted edits show *"Modified — not committed"* in amber
- **Fast & async** — git blame runs in the background with a 5-second timeout; results are cached per file

## How it works

Move your cursor to any line in a file that is inside a git repository. The blame info appears at the end of that line:

```
int result = Compute(x, y);        mwaseem  •  #a3f9c12  •  3 days ago  •  Fix overflow in edge case
```

The adornment clears automatically when you move to a different line or edit the file.

## Requirements

- Visual Studio 2022 (17.14+) or Visual Studio 2026
- A git repository with at least one commit
- `git` available on the system `PATH`

## Extension Settings

No configuration required. The extension activates automatically for every text editor window.

## Known Limitations

- Binary files and files outside a git repository show no adornment
- Very large files with slow `git blame` responses (> 5 seconds) will silently skip the annotation

## License

[MIT](LICENSE)
