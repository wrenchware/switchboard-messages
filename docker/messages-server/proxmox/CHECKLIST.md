# Checking install-proxmox.sh on a Proxmox host

A run from nothing to a phone joined and the Facebook test account signed in, on a fresh container. Use the Facebook
test account only: no real account is signed in on this test server, and nothing is sent anywhere but the test thread.

The setup code, the first-run steps and the QR code (part 3) come with the server's first run (10.4) and the Tailscale
screen (10.8). With a package from before them, set the page password at the script's question instead, sign Tailscale
in with the link the script prints, and add the phone with the pairing code.

Have ready: the package `messages-server-<version>-linux-x64.tar.gz` and its `.sha256` (from
`tools\Build-ServerTarball.ps1`, in `artifacts\server-tarball`), the phone with the Play internal-testing build of
Messages, and a PC browser.

## 1. Make the container

1. Copy the package and its `.sha256` to the Proxmox host, for example with WinSCP or
   `scp messages-server-<version>-linux-x64.tar.gz* root@<proxmox>:/root/`.
2. Copy `docker/messages-server/proxmox/install-proxmox.sh` there too (until the script has a public address).
3. In the Proxmox web interface: the node, **Shell**. Run:
   `bash /root/install-proxmox.sh --tarball /root/messages-server-<version>-linux-x64.tar.gz`
4. Take every default, except: answer the backups question with a test folder such as `/root/messages-backups-test`,
   and leave the Tailscale auth key and the page password empty (Enter).
5. Note how long it took and whether any question was unclear.

Report: the last 20 lines it printed (the setup code and pairing code there are for a test server and expire, but leave
out the Tailscale link), and anything it stopped on.

## 2. The container

In the host's shell (`<id>` is the container's number it printed):

```sh
pct config <id> | grep -E 'features|unprivileged|mp0|tags'
pct exec <id> -- systemctl is-active messages-server messages-server-update.timer
pct exec <id> -- journalctl -u messages-server --no-pager -o cat | grep -E 'server google|google chrome|sandbox|tailnet state|server started|refused'
pct exec <id> -- stat -c '%U %a %n' /var/lib/messages-server/backups
ls -ln /root/messages-backups-test -d
```

Report all of it. The two things this run is for:

- **Chrome's sandbox in an unprivileged container.** Wanted: `server google enabled browser=chrome sandbox=on`, then,
  about a minute after Chrome starts, `google chrome sandbox=active`. If it says `sandbox_unavailable detail=...`,
  report the detail word and the output of
  `cat /proc/sys/user/max_user_namespaces /proc/sys/kernel/apparmor_restrict_unprivileged_userns` on the host. Do not
  turn the sandbox off to make it start.
- **The backups folder**: owned by `messages-server` with mode 700 inside, and by `102787` on the host.

## 3. The page and the setup

1. On the PC, open the page address it printed (`https://<address>:8787/`). Accept the certificate warning once.
2. Enter the setup code and choose the page's password.
3. Follow the page's first-run steps:
   - **Tailscale**: sign in with your own Tailscale account. Wanted: the step turns green by itself, and the machine
     `messages-server` appears in Tailscale's admin console. Report anything the checklist on that step says is left.
   - **Accounts**: connect **Facebook (Messenger) with the test account only**. Report whether the sign-in window
     showed and finished, and any "Was this you?" prompt.
   - Skip Google this time unless the sandbox line above said `active`; if it did, report whether the Google sign-in
     screen shows on the page (do not sign a real Google account in).
4. **Add a phone**: on the page, show the QR code. On the phone, in Messages, scan it. Wanted: the phone signs in to
   Tailscale, joins without typing a code, and shows the test account's conversations.
5. On the phone, open the owner-designated Messenger test thread and send one message there. Nowhere else.

Report: each step's result, the time from scanning to conversations shown, and the phone's Settings, Tailscale section
(direct or relayed).

## 4. Running it again (update)

Run the same command again. Wanted: it finds the container, asks to update it, and the server comes back with the
test account still signed in and the phone still joined (no new pairing). Report what it printed.

## 5. The nightly update and backups

```sh
pct exec <id> -- systemctl start messages-server-update
pct exec <id> -- journalctl -u messages-server-update --no-pager -o cat | tail -n 5
ls -l /root/messages-backups-test
```

Wanted: "no release address", and with Google on a line about Chrome. Report the lines, and whether a backup file
appears in the test folder after the page's backups step has been run.

## 6. Remove it

1. On the page, remove the Facebook test account (so Facebook forgets the server's device).
2. Remove the `messages-server` machine in Tailscale's admin console.
3. `pct stop <id> && pct destroy <id>`, then `rm -rf /root/messages-backups-test`.

Report anything that was left behind.
