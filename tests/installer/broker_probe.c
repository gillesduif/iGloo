/* Static target fixture. No legitimate call can reach a physical block device.
 * Forbidden syscall checks run only AFTER the parent verified effective isolation.
 */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <linux/capability.h>
#include <sched.h>
#include <signal.h>
#include <stdio.h>
#include <string.h>
#include <sys/mount.h>
#include <sys/reboot.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/sysmacros.h>
#include <sys/types.h>
#include <unistd.h>

static int denied(long value) { return value == -1 && errno == EPERM; }
int main(int argc, char **argv)
{
    if (argc != 2) return 90;
    const char *test = argv[1];
    if (!strcmp(test, "success")) return 0;
    if (!strcmp(test, "mount")) return denied(mount("tmpfs", "/tmp", "tmpfs", 0, NULL)) ? 0 : 1;
    if (!strcmp(test, "remount")) return denied(mount(NULL, "/", NULL, MS_REMOUNT, NULL)) ? 0 : 2;
    if (!strcmp(test, "efivarfs")) return denied(mount("efivarfs", "/sys", "efivarfs", 0, NULL)) ? 0 : 3;
    if (!strcmp(test, "reboot")) return denied(reboot(RB_AUTOBOOT)) ? 0 : 4;
    if (!strcmp(test, "module")) return denied(syscall(SYS_finit_module, -1, "", 0)) ? 0 : 5;
    if (!strcmp(test, "mknod")) return denied(mknod("/tmp/block", S_IFBLK | 0600, makedev(4095, 1048575))) ? 0 : 6;
    if (!strcmp(test, "namespace")) return denied(unshare(CLONE_NEWUSER | CLONE_NEWNS)) ? 0 : 7;
    if (!strcmp(test, "chroot")) return denied(chroot("/")) ? 0 : 20;
    if (!strcmp(test, "bootstrap-null")) return denied(mknod("/tmp/test-dev-null", S_IFCHR | 0600, makedev(1, 3))) ? 0 : 21;
    if (!strcmp(test, "regain-capability")) {
        struct __user_cap_header_struct header = { _LINUX_CAPABILITY_VERSION_3, 0 };
        struct __user_cap_data_struct data[2] = { { 1U << CAP_SYS_ADMIN, 1U << CAP_SYS_ADMIN, 0 }, { 0, 0, 0 } };
        return denied(syscall(SYS_capset, &header, data)) ? 0 : 19;
    }
    if (!strcmp(test, "network")) return denied(socket(AF_INET, SOCK_STREAM, 0)) ? 0 : 8;
    if (!strcmp(test, "firmware")) return open("/sys/firmware/efi/efivars/igloo-test-fixture", O_WRONLY | O_CREAT, 0600) == -1 && errno == ENOENT ? 0 : 9;
    if (!strcmp(test, "service")) return access("/run/dbus/system_bus_socket", F_OK) == -1 && errno == ENOENT ? 0 : 10;
    if (!strcmp(test, "raw")) return open("/dev/windows-fixture", O_RDWR) == -1 && errno == ENOENT ? 0 : 11;
    if (!strcmp(test, "esp")) return open("/boot/efi/forbidden", O_WRONLY | O_CREAT, 0600) == -1 && errno == EROFS ? 0 : 12;
    if (!strcmp(test, "payload")) return open("/run/igloo-source/forbidden", O_WRONLY | O_CREAT, 0600) == -1 && errno == EROFS ? 0 : 13;
    if (!strcmp(test, "file-source")) {
        int fd = open("/run/igloo-source/source", O_RDONLY); char byte;
        int result = fd >= 0 && read(fd, &byte, 1) == 1 && byte == 'a';
        if (fd >= 0) close(fd);
        return result ? 0 : 14;
    }
    if (!strcmp(test, "write")) {
        int fd = open("/etc/result", O_WRONLY | O_CREAT | O_EXCL, 0600);
        if (fd < 0) return 15;
        int result = write(fd, "ok", 2) == 2 && fsync(fd) == 0;
        close(fd);
        return result ? 0 : 16;
    }
    if (!strcmp(test, "die")) { raise(SIGKILL); return 17; }
    if (!strcmp(test, "timeout")) { sleep(30); return 18; }
    return 91;
}
