"""A running accounts-daemon is told about the account picture; a failure there is not fatal."""

import importlib.util
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]


def load(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


AGENTS = {
    "debian-family": load("account_picture_debian_agent", "distros/_debian-family/agent/agent.py"),
    "fedora-kde": load("account_picture_fedora_agent", "distros/fedora-kde/agent/agent.py"),
}
ICON = Path("/var/lib/AccountsService/icons/lab")
USER_OBJECT = "/org/freedesktop/Accounts/User1000"


def bus(find=(0, f'o "{USER_OBJECT}"\n'), set_icon=0):
    calls = []

    def run(argv, **kwargs):
        calls.append((list(argv), kwargs))
        if "FindUserByName" in argv:
            return mock.Mock(returncode=find[0], stdout=find[1], stderr="")
        if "SetIconFile" in argv:
            return mock.Mock(returncode=set_icon, stdout="", stderr="")
        return mock.Mock(returncode=0, stdout="", stderr="")

    return calls, run


class AccountPictureDaemonTests(unittest.TestCase):
    def test_the_daemon_is_asked_for_the_user_and_given_the_icon(self):
        for agent in AGENTS.values():
            calls, run = bus()
            with mock.patch.object(agent.shutil, "which", return_value="/usr/bin/busctl"), \
                 mock.patch.object(agent, "run_cmd", side_effect=run), \
                 mock.patch.object(agent, "logger") as log:
                agent._tell_accounts_daemon("lab", ICON)
            self.assertEqual(2, len(calls))
            find, told = (argv for argv, _ in calls)
            self.assertEqual(["busctl", "--system", "--timeout=20", "call", "org.freedesktop.Accounts",
                              "/org/freedesktop/Accounts", "org.freedesktop.Accounts",
                              "FindUserByName", "s", "lab"], find)
            self.assertEqual(["busctl", "--system", "--timeout=20", "call", "org.freedesktop.Accounts",
                              USER_OBJECT, "org.freedesktop.Accounts.User",
                              "SetIconFile", "s", str(ICON)], told)
            self.assertTrue(all(kwargs.get("check") is False for _, kwargs in calls))
            log.warning.assert_not_called()

    def test_an_unknown_user_ends_without_the_second_call(self):
        for find in ((1, ""), (0, ""), (0, 'o "/org/freedesktop/Other/User1000"\n'), (0, 's "text"\n')):
            for agent in AGENTS.values():
                calls, run = bus(find=find)
                with mock.patch.object(agent.shutil, "which", return_value="/usr/bin/busctl"), \
                     mock.patch.object(agent, "run_cmd", side_effect=run), \
                     mock.patch.object(agent, "logger") as log:
                    agent._tell_accounts_daemon("lab", ICON)
                self.assertEqual(1, len(calls))
                log.warning.assert_called_once()

    def test_a_refusal_is_logged_and_not_raised(self):
        for agent in AGENTS.values():
            calls, run = bus(set_icon=1)
            with mock.patch.object(agent.shutil, "which", return_value="/usr/bin/busctl"), \
                 mock.patch.object(agent, "run_cmd", side_effect=run), \
                 mock.patch.object(agent, "logger") as log:
                agent._tell_accounts_daemon("lab", ICON)
            self.assertEqual(2, len(calls))
            log.warning.assert_called_once()

    def test_a_missing_tool_or_a_silent_bus_is_not_raised(self):
        for agent in AGENTS.values():
            with mock.patch.object(agent.shutil, "which", return_value=None), \
                 mock.patch.object(agent, "run_cmd") as run, \
                 mock.patch.object(agent, "logger"):
                agent._tell_accounts_daemon("lab", ICON)
            run.assert_not_called()
            for failure in (subprocess.TimeoutExpired("busctl", 60), OSError("no bus")):
                with mock.patch.object(agent.shutil, "which", return_value="/usr/bin/busctl"), \
                     mock.patch.object(agent, "run_cmd", side_effect=failure), \
                     mock.patch.object(agent, "logger") as log:
                    agent._tell_accounts_daemon("lab", ICON)
                log.warning.assert_called_once()

    def test_the_daemon_is_told_after_the_files_are_written(self):
        for name, agent in AGENTS.items():
            with self.subTest(agent=name), TemporaryDirectory() as temporary:
                root = Path(temporary)
                (root / "home/lab").mkdir(parents=True)
                (root / "opt/igloo").mkdir(parents=True)
                (root / "opt/igloo/igloo-avatar.jpg").write_bytes(b"avatar marker")

                def resolve(path, root=root):
                    value = str(path)
                    for prefix, target in (("/opt/igloo", "opt/igloo"), ("/home", "home"),
                                           ("/var/lib/AccountsService", "accounts")):
                        if value == prefix or value.startswith(prefix + "/"):
                            return root / target / value[len(prefix):].lstrip("/")
                    return Path(value)

                seen = {}

                def tell(username, icon_path, root=root, seen=seen):
                    seen["user"] = username
                    seen["icon"] = icon_path
                    seen["icon written"] = icon_path.read_bytes()
                    seen["face written"] = (root / "home/lab/.face").read_bytes()

                patches = [mock.patch.object(agent, "Path", side_effect=resolve),
                           mock.patch.object(agent, "run_cmd", return_value=mock.Mock(returncode=0, stdout="")),
                           mock.patch.object(agent, "logger"),
                           mock.patch.object(agent, "_tell_accounts_daemon", side_effect=tell)]
                if hasattr(agent, "_selected_media_source"):
                    patches.append(mock.patch.object(agent, "_selected_media_source",
                                                     return_value=root / "opt/igloo/igloo-avatar.jpg"))
                for patch in patches:
                    patch.start()
                try:
                    agent.migrate_account_picture({
                        "accountPicture": {"fileName": "igloo-avatar.jpg"},
                        "user": {"preferredLinuxUsername": "lab"},
                    })
                finally:
                    for patch in reversed(patches):
                        patch.stop()

                self.assertEqual("lab", seen["user"])
                self.assertEqual(root / "accounts/icons/lab", seen["icon"])
                self.assertEqual(b"avatar marker", seen["icon written"])
                self.assertEqual(b"avatar marker", seen["face written"])


if __name__ == "__main__":
    unittest.main()
