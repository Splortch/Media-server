const fs = require('node:fs');
function stamp(root, version, msi) {
  if (!/^0\.1\.0-alpha\.[1-9][0-9]{0,4}$/.test(version)) throw new Error('Invalid Windows alpha version.');
  const number = Number(version.split('.').at(-1));
  if (number > 65535 || msi !== `0.1.${number}`) throw new Error('Invalid MSI version.');
  const path = `${root}/clients/windows/src-tauri`;
  const config = JSON.parse(fs.readFileSync(`${path}/tauri.conf.json`, 'utf8'));
  config.version = version;
  config.bundle.windows.wix.version = msi;
  const cargo = fs.readFileSync(`${path}/Cargo.toml`, 'utf8');
  const lock = fs.readFileSync(`${path}/Cargo.lock`, 'utf8');
  const packagePattern = /(\[package\]\nname = "media-server-windows-client"\nversion = ")[^"]+("\n)/;
  const lockPattern = /(\[\[package\]\]\nname = "media-server-windows-client"\nversion = ")[^"]+("\n)/;
  if (!packagePattern.test(cargo) || !lockPattern.test(lock)) throw new Error('Windows package metadata was not found.');
  fs.writeFileSync(`${path}/tauri.conf.json`, JSON.stringify(config, null, 2) + '\n');
  fs.writeFileSync(`${path}/Cargo.toml`, cargo.replace(packagePattern, (_, before, after) => before + version + after));
  fs.writeFileSync(`${path}/Cargo.lock`, lock.replace(lockPattern, (_, before, after) => before + version + after));
}
if (require.main === module) stamp(process.cwd(), process.env.CLIENT_VERSION, process.env.MSI_VERSION);
module.exports = { stamp };
