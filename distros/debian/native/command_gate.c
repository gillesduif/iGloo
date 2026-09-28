/* Trusted pre-exec barrier. Build as a static amd64 executable and pin its hash
 * in the deployment runtime. No package code runs before independent readback.
 * This file does not implement a sandbox: bubblewrap and seccomp precede it.
 */
#define _GNU_SOURCE
#include <signal.h>
#include <linux/capability.h>
#include <sched.h>
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include <sys/mount.h>
#include <sys/prctl.h>
#include <sys/syscall.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    if (argc >= 3 && !strcmp(argv[1], "--supervise")) {
        /* Only the trusted outer process reaches this branch with mount caps.
         * Make inherited mounts private BEFORE bwrap imports individual nodes.
         * No physical device is opened or mounted by this operation.
         */
        if (argv[2][0] != '/' || unshare(CLONE_NEWNS) != 0) return 125;
        if (mount(NULL, "/", NULL, MS_REC | MS_PRIVATE, NULL) != 0) return 125;
        execv(argv[2], argv + 2);
        return 126;
    }
    if (argc < 3 || argv[2][0] != '/') return 125;
    char *end = NULL;
    unsigned long mask = strtoul(argv[1], &end, 16);
    if (!end || *end || (mask != 0 && mask != 0xdbUL && mask != 0x800000dbUL)) return 125;
    /* bwrap's NoNewPrivs is necessary but not a claim that its bounding set was
     * cleared on every runtime. SETPCAP belongs to this trusted gate only.
     */
    for (unsigned int cap = 0; cap < 64; cap++) {
        int exists = prctl(PR_CAPBSET_READ, cap, 0, 0, 0);
        if (exists < 0) break;
        if (!(mask & (1UL << cap)) && prctl(PR_CAPBSET_DROP, cap, 0, 0, 0) != 0) return 125;
    }
    struct __user_cap_header_struct header = { _LINUX_CAPABILITY_VERSION_3, 0 };
    struct __user_cap_data_struct data[2] = {
        { (uint32_t)mask, (uint32_t)mask, (uint32_t)mask }, { 0, 0, 0 }
    };
    if (syscall(SYS_capset, &header, data) != 0) return 125;
    /* Never expose setup descriptors, raw devices, mount FDs or service pipes. */
    if (close_range(3, ~0U, 0) != 0) return 125;
    if (prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0) != 0) return 125;
    if (raise(SIGSTOP) != 0) return 125;
    execv(argv[2], argv + 2);
    return 126;
}
