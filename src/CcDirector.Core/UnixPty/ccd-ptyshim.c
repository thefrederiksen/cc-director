/*
 * ccd-ptyshim - acquire the pseudo-terminal as the controlling terminal, then
 * replace this process with the real program.
 *
 * Why this exists: the Director spawns agents via posix_spawn with
 * POSIX_SPAWN_SETSID and the pseudo-terminal subordinate opened onto standard
 * input. On Linux that open is enough - a session leader that opens a terminal
 * automatically acquires it as its controlling terminal. On macOS it is NOT:
 * the kernel requires an explicit TIOCSCTTY ioctl, issued by the child itself,
 * between session creation and exec. posix_spawn has no file action for an
 * ioctl, so the acquisition must happen in a real child process - this shim.
 *
 * Without a controlling terminal the terminal has no foreground process group,
 * so a resize (TIOCSWINSZ on the master) updates the kernel's size record but
 * the window-change signal is delivered to nobody. The agent then keeps
 * painting at its spawn width forever, and every viewer that mirrors the new
 * width renders overlapping garbage - the garbled-terminal bug on macOS.
 *
 * Usage: ccd-ptyshim <program> [args...]
 * Standard input must already be the pseudo-terminal subordinate, and the
 * process must already be a session leader (POSIX_SPAWN_SETSID).
 *
 * Exit codes: 125 = shim precondition failed, 127 = exec failed.
 * Errors are written to standard error, which is the terminal itself, so a
 * failure is visible in the session view and in the raw capture.
 */

#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

int main(int argc, char *argv[])
{
    if (argc < 2) {
        fprintf(stderr, "ccd-ptyshim: usage: ccd-ptyshim <program> [args...]\n");
        return 125;
    }

    if (ioctl(STDIN_FILENO, TIOCSCTTY, 0) == -1) {
        fprintf(stderr, "ccd-ptyshim: TIOCSCTTY on standard input failed: %s\n",
                strerror(errno));
        return 125;
    }

    /* execvp, not execv: the Director usually passes an absolute path, but when its
     * resolver falls back to a bare command name the old direct posix_spawnp call
     * would search PATH - the shim must behave identically. */
    execvp(argv[1], &argv[1]);
    fprintf(stderr, "ccd-ptyshim: execvp '%s' failed: %s\n", argv[1], strerror(errno));
    return 127;
}
