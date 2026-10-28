#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Signed LAN requests (protocol 2). The pairing token never crosses the network:
 * each authenticated discovery reply carries a fresh challenge, and every HTTP or
 * WebSocket request carries
 *
 *   Authorization: ClawPal-HMAC c=<challenge>,n=<counter>,s=<signature>
 *   X-ClawPal-Body-SHA256: <body hash>   (optional; absent means an empty body)
 *
 * signature = HMAC-SHA256(token, "clawpal-auth-v2\n" METHOD "\n" URI "\n"
 *                                challenge "\n" counter "\n" body-hash), lowercase hex.
 * A counter must exceed the last one accepted for its challenge, so a captured
 * request cannot be replayed; challenges expire and do not survive a restart.
 * The companion's DeviceAuth implements the other side. */

#define BUDDY_AUTH_CHALLENGES 16
#define BUDDY_AUTH_CHALLENGE_US (10LL * 60 * 1000 * 1000)
#define BUDDY_AUTH_EMPTY_BODY "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

typedef enum {
    BUDDY_AUTH_OK,
    BUDDY_AUTH_MALFORMED,
    BUDDY_AUTH_UNKNOWN_CHALLENGE,
    BUDDY_AUTH_BAD_SIGNATURE,
    BUDDY_AUTH_REPLAYED,
} buddy_auth_result_t;

/* `token` is the 64-character pairing token. `lock`/`unlock` serialize the
 * discovery and HTTP tasks; they may block, so pass a mutex, not a spinlock. */
void buddy_auth_init(const char *token, void (*lock)(void), void (*unlock)(void));

/* Challenge for a discovery reply; writes 32 hex + NUL. The companion sends each
 * discovery to several addresses, so a repeated `nonce` gets its earlier challenge
 * back; otherwise one is made from 16 random bytes, replacing the oldest. */
void buddy_auth_challenge(const char *nonce, const uint8_t random_bytes[16], int64_t now_us, char out[33]);

/* Discovery proof for protocol 2: HMAC(token, nonce|id|port|challenge), 64 hex + NUL. */
bool buddy_auth_hello_proof(const char *nonce, const char *id, int port, const char *challenge, char out[65]);

/* Checks one request. `body_sha256` is the declared body hash (64 lowercase hex);
 * the handler must still compare it with the bytes it reads. */
buddy_auth_result_t buddy_auth_verify(const char *authorization, const char *method, const char *uri,
                                      const char *body_sha256, int64_t now_us);

/* SHA-256 of `length` bytes as 64 lowercase hex + NUL. */
bool buddy_auth_body_hash(const void *data, size_t length, char out[65]);

bool buddy_auth_is_hex(const char *value, size_t length);
