#define _GNU_SOURCE
#include <wayland-client.h>
#include <xkbcommon/xkbcommon.h>
#include <sys/mman.h>
#include <unistd.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

/* Test-only virtual keyboard with an evdev keymap, unlike wtype's synthetic keycodes. */
static struct wl_seat *seat;
static struct wl_proxy *manager;
static const struct wl_interface keyboard_interface;
static const struct wl_interface *create_types[] = { &wl_seat_interface, &keyboard_interface };
static const struct wl_message manager_requests[] = { { "create_virtual_keyboard", "on", create_types } };
static const struct wl_interface manager_interface = { "zwp_virtual_keyboard_manager_v1", 1, 1, manager_requests, 0, NULL };
static const struct wl_message keyboard_requests[] = {
    { "keymap", "uhu", NULL }, { "key", "uuu", NULL },
    { "modifiers", "uuuu", NULL }, { "destroy", "", NULL }
};
static const struct wl_interface keyboard_interface = { "zwp_virtual_keyboard_v1", 1, 4, keyboard_requests, 0, NULL };
static void global(void *data, struct wl_registry *registry, uint32_t name, const char *interface, uint32_t version) {
    (void)data; (void)version;
    if (!strcmp(interface, "wl_seat")) seat = wl_registry_bind(registry, name, &wl_seat_interface, 1);
    if (!strcmp(interface, manager_interface.name)) manager = wl_registry_bind(registry, name, &manager_interface, 1);
}
static void removed(void *data, struct wl_registry *registry, uint32_t name) { (void)data; (void)registry; (void)name; }
static const struct wl_registry_listener listener = { global, removed };
static uint32_t now_ms(void) {
    struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint32_t)((uint64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000);
}
static void key(struct wl_display *display, struct wl_proxy *keyboard, uint32_t code, uint32_t state) {
    wl_proxy_marshal_flags(keyboard, 1, NULL, 1, 0, now_ms(), code, state);
    wl_display_roundtrip(display);
}
int main(int argc, char **argv) {
    if ((argc < 2 || argc > 4) || strncmp(argv[1], "/tmp/wsn-", 9)) return 64;
    struct wl_display *display = wl_display_connect(argv[1]);
    if (!display) return 1;
    struct wl_registry *registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &listener, NULL); wl_display_roundtrip(display);
    if (!seat || !manager) return 2;
    struct wl_proxy *keyboard = wl_proxy_marshal_flags(manager, 0, &keyboard_interface, 1, 0, seat, NULL);
    struct xkb_context *context = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
    struct xkb_rule_names names = { .rules = "evdev", .model = "pc105", .layout = "us" };
    struct xkb_keymap *map = xkb_keymap_new_from_names(context, &names, XKB_KEYMAP_COMPILE_NO_FLAGS);
    if (!map) return 3;
    char *text = xkb_keymap_get_as_string(map, XKB_KEYMAP_FORMAT_TEXT_V1);
    size_t size = strlen(text) + 1;
    int fd = memfd_create("wayscrawl-keymap", MFD_CLOEXEC);
    if (fd < 0 || write(fd, text, size) != (ssize_t)size) return 4;
    wl_proxy_marshal_flags(keyboard, 0, NULL, 1, 0, 1u, fd, (uint32_t)size);
    wl_display_roundtrip(display); close(fd); free(text);
    int control = argc == 4 && !strcmp(argv[3], "control");
    uint32_t hold_key = control ? 29u : 42u, mask = control ? 4u : 1u;
    printf("modifier press monotonic_ms=%u\n", now_ms()); fflush(stdout);
    key(display, keyboard, hold_key, 1);
    wl_proxy_marshal_flags(keyboard, 2, NULL, 1, 0, mask, 0u, 0u, 0u); wl_display_roundtrip(display);
    int delay = argc >= 3 ? atoi(argv[2]) : 150;
    if (delay < 0 || delay > 12000) return 64;
    usleep((useconds_t)delay * 1000);
    key(display, keyboard, control ? 44u : 30u, 1); key(display, keyboard, control ? 44u : 30u, 0);

    key(display, keyboard, hold_key, 0);
    wl_proxy_marshal_flags(keyboard, 2, NULL, 1, 0, 0u, 0u, 0u, 0u); wl_display_roundtrip(display);
    key(display, keyboard, 28, 1); key(display, keyboard, 28, 0);
    wl_proxy_marshal_flags(keyboard, 3, NULL, 1, WL_MARSHAL_FLAG_DESTROY); wl_display_roundtrip(display);
    xkb_keymap_unref(map); xkb_context_unref(context); wl_display_disconnect(display);
    return 0;
}
