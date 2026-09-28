# Read-only installer inventory boundary

`collect_inventory.py` emits the versioned JSON parsed by
`Igloo.Core.Preparation.LinuxInstallerInventoryProtocol`. It only observes using
explicit read-only util-linux operations. It is not yet packaged into production
installer media and does not authorize mounting or installation.

Run deterministic fixtures without inspecting the current machine:

```text
python -B -m unittest discover -s tests/installer -p "test_*.py"
```

The pure Core resolver must validate the parsed observation against the durably
reopened prepared generation. Available acquisition alone is not Exact ownership.
Unknown topology, command failure or changed independent passes fail closed.

See [installation ownership](../../../docs/architecture/community-installation-ownership.md)
for support limits and the preparation enablement gate.
