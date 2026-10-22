# Messages Server on a Linux host without Docker

This package runs Messages Server as a systemd service on an x86-64 Linux host: Ubuntu, Debian, or a Proxmox LXC. It is
the same server as the container, with Messenger and Instagram. Google Messages and Google Voice are not in it: on Linux
they run beside the server's own Google Chrome, which only the Google container image carries.

Nothing needs installing first: .NET is inside the package. It does use the host's ICU, OpenSSL 3, CA certificates and
time zone data, and `install.sh` adds whichever of them is missing with `apt-get` or `dnf`.

## Install or update

```sh
tar -xzf messages-server-<version>-linux-x64.tar.gz
sudo ./messages-server-<version>-linux-x64/install.sh
```

It makes the account `messages-server`, puts the program in `/opt/messages-server` and starts `messages-server.service`.
Running a newer package's `install.sh` updates in place: the server is stopped (pending sends finish first), replaced and
started again with the same sign-ins and paired devices.

## Settings

`/etc/messages-server/environment` (root only), then `sudo systemctl restart messages-server`:

- `MESSAGES_HOSTNAMES`: the names and addresses devices reach the server by (for example `server.lan,192.168.1.20`).
  They go into the certificate made on the first start, so set them before then.
- `MESSAGES_PORT`: the HTTPS port, 8787 by default. Allow it in the firewall for the home network only.
- `MESSAGES_ADMIN_PASSWORD`: turns on the server's own page at `https://<this server>:8787/` (12 characters or more,
  used nowhere else).

## Pairing code and fingerprint

Both are printed when the server starts, the code only while no device is paired:

```sh
sudo journalctl -u messages-server --no-pager | grep -E "pairing code=|tls fingerprint"
```

In Messages: Settings → Messages Server, with the server's **Address** (and port, if not 8787), the **Fingerprint**
and the **Pairing code**. The code works once, for 10 minutes; a new one is printed when it runs out.

## Where things live

- `/var/lib/messages-server/keys`: the sealing keys, the TLS certificate's private key, and everything the Messenger
  helper keeps, including this server's Messenger history.
- `/var/lib/messages-server/data`: sign-ins, paired devices, the server's stores and its log, `server.log`. Everything
  secret in it is sealed with the keys, so it is useless without them.

Both are private to the account. Back them up together, with the server stopped:

```sh
sudo systemctl stop messages-server
sudo tar -czf messages-server-backup.tar.gz -C /var/lib messages-server
sudo systemctl start messages-server
```

## Limits

The service has the container's limits: 2 GB of memory with no swap beyond it, and 1024 threads. To change them,
`sudo systemctl edit messages-server` and set `MemoryMax=` or `TasksMax=` under `[Service]`. It runs with no privileges
or capabilities and can write only its own folder in `/var/lib`.

## Remove it

```sh
sudo /opt/messages-server/uninstall.sh          # the program and service; the data, keys and settings stay
sudo /opt/messages-server/uninstall.sh --all    # also the data, keys, settings and account
```

Remove the accounts from Manage server first if the server should sign out of them.
