"""An identical monitor pair cannot be assigned by its shared PnP product ID."""

import importlib.util
import json
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase, main, mock


SOURCE = Path(__file__).resolve().parents[2] / "distros/fedora-kde/agent/display-apply.py"
SPEC = importlib.util.spec_from_file_location("fedora_display_apply", SOURCE)
APPLIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(APPLIER)


class FedoraDisplayIdentityTest(TestCase):
    def test_missing_or_unmatched_selected_layout_never_marks_success(self):
        with TemporaryDirectory() as temporary:
            layout = Path(temporary) / "display-layout.json"
            with mock.patch.object(APPLIER.sys, "argv",
                                   ["display-apply.py", "--layout", str(layout)]), \
                 mock.patch.object(APPLIER, "log"):
                self.assertEqual(1, APPLIER.main())

            layout.write_text(json.dumps([{"pnpId": "ABC1234", "width": 1920,
                                           "height": 1080}]), encoding="utf-8")
            with mock.patch.object(APPLIER.sys, "argv",
                                   ["display-apply.py", "--layout", str(layout)]), \
                 mock.patch.object(APPLIER, "wait_for_kscreen", return_value=True), \
                 mock.patch.object(APPLIER, "connector_by_pnp", return_value={}), \
                 mock.patch.object(APPLIER, "log"):
                self.assertEqual(1, APPLIER.main())

    def test_identical_panels_are_not_mapped_to_one_connector(self):
        with TemporaryDirectory() as temporary:
            drm = Path(temporary)
            edid = bytearray(128)
            edid[8:10] = ((1 << 10) | (2 << 5) | 3).to_bytes(2, "big")
            edid[10:12] = (0x1234).to_bytes(2, "little")
            for connector in ("DP-1", "DP-2"):
                card = drm / f"card0-{connector}"
                card.mkdir()
                (card / "status").write_text("connected\n", encoding="ascii")
                (card / "edid").write_bytes(edid)
            real_path = Path
            with mock.patch.object(APPLIER, "Path", side_effect=lambda path: (
                    drm if path == "/sys/class/drm" else real_path(path))), \
                 mock.patch.object(APPLIER, "log"):
                self.assertEqual({}, APPLIER.connector_by_pnp())

            (drm / "card0-DP-2/edid").write_bytes(edid[:10] + b"\x35\x12" + edid[12:])
            with mock.patch.object(APPLIER, "Path", side_effect=lambda path: (
                    drm if path == "/sys/class/drm" else real_path(path))), \
                 mock.patch.object(APPLIER, "log"):
                self.assertEqual({"ABC1234": "DP-1", "ABC1235": "DP-2"},
                                 APPLIER.connector_by_pnp())

    def test_duplicate_staged_identity_never_applies_to_one_connector(self):
        with TemporaryDirectory() as temporary:
            layout = Path(temporary) / "display-layout.json"
            layout.write_text(json.dumps([
                {"pnpId": "ABC1234", "width": 1920, "height": 1080, "primary": True},
                {"pnpId": "ABC1234", "width": 1920, "height": 1080, "x": 1920},
            ]), encoding="utf-8")
            with mock.patch.object(APPLIER.sys, "argv",
                                   ["display-apply.py", "--layout", str(layout)]), \
                 mock.patch.object(APPLIER, "wait_for_kscreen", return_value=True), \
                 mock.patch.object(APPLIER, "connector_by_pnp",
                                   return_value={"ABC1234": "DP-1"}), \
                 mock.patch.object(APPLIER.subprocess, "run") as run, \
                 mock.patch.object(APPLIER, "log"):
                self.assertEqual(1, APPLIER.main())
                run.assert_not_called()


if __name__ == "__main__":
    main()
