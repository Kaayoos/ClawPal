#include "buddy_auth.h"

#include <stdio.h>
#include <string.h>

#include "mbedtls/md.h"
#include "mbedtls/sha256.h"

typedef struct {
    char nonce[33];
    char challenge[33];
    uint32_t last_counter;
    int64_t expires_us;
} auth_challenge_t;

static char token[65];
static auth_challenge_t challenges[BUDDY_AUTH_CHALLENGES];
static unsigned next_slot;
static void no_lock(void) {}
static void (*lock)(void) = no_lock;
static void (*unlock)(void) = no_lock;

bool buddy_auth_is_hex(const char *value, size_t length)
{
    if (!value || strlen(value) != length) return false;
    for (size_t i = 0; i < length; ++i)
        if (!((value[i] >= '0' && value[i] <= '9') || (value[i] >= 'a' && value[i] <= 'f'))) return false;
    return true;
}

static void hex(const uint8_t *bytes, size_t length, char *out)
{
    static const char digits[] = "0123456789abcdef";
    for (size_t i = 0; i < length; ++i) {
        out[i * 2] = digits[bytes[i] >> 4];
        out[i * 2 + 1] = digits[bytes[i] & 15];
    }
    out[length * 2] = '\0';
}

static bool hmac_hex(const char *message, size_t length, char out[65])
{
    uint8_t digest[32];
    const mbedtls_md_info_t *algorithm = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
    if (!algorithm || mbedtls_md_hmac(algorithm, (const uint8_t *)token, 64,
                                      (const uint8_t *)message, length, digest) != 0) return false;
    hex(digest, sizeof(digest), out);
    return true;
}

void buddy_auth_init(const char *value, void (*lock_function)(void), void (*unlock_function)(void))
{
    lock = lock_function ? lock_function : no_lock;
    unlock = unlock_function ? unlock_function : no_lock;
    lock();
    strlcpy(token, value, sizeof(token));
    memset(challenges, 0, sizeof(challenges));
    next_slot = 0;
    unlock();
}

void buddy_auth_challenge(const char *nonce, const uint8_t random_bytes[16], int64_t now_us, char out[33])
{
    lock();
    for (size_t i = 0; i < BUDDY_AUTH_CHALLENGES; ++i) {
        if (challenges[i].expires_us > now_us && strcmp(challenges[i].nonce, nonce) == 0) {
            strlcpy(out, challenges[i].challenge, 33);
            unlock();
            return;
        }
    }
    hex(random_bytes, 16, out);
    auth_challenge_t *slot = &challenges[next_slot];
    next_slot = (next_slot + 1) % BUDDY_AUTH_CHALLENGES;
    strlcpy(slot->nonce, nonce, sizeof(slot->nonce));
    strlcpy(slot->challenge, out, sizeof(slot->challenge));
    slot->last_counter = 0;
    slot->expires_us = now_us + BUDDY_AUTH_CHALLENGE_US;
    unlock();
}

bool buddy_auth_hello_proof(const char *nonce, const char *id, int port, const char *challenge, char out[65])
{
    if (!buddy_auth_is_hex(nonce, 32) || !buddy_auth_is_hex(id, 12) || !buddy_auth_is_hex(challenge, 32)) return false;
    char message[32 + 1 + 12 + 1 + 5 + 1 + 32 + 1];
    int length = snprintf(message, sizeof(message), "%.32s|%.12s|%d|%.32s", nonce, id, port, challenge);
    return length > 0 && (size_t)length < sizeof(message) && hmac_hex(message, (size_t)length, out);
}

/* "ClawPal-HMAC c=<32 hex>,n=<1-4294967295>,s=<64 hex>" with nothing else. */
static bool parse_authorization(const char *value, char challenge[33], uint32_t *counter, char signature[65])
{
    static const char prefix[] = "ClawPal-HMAC c=";
    if (!value || strncmp(value, prefix, sizeof(prefix) - 1) != 0) return false;
    value += sizeof(prefix) - 1;
    if (strnlen(value, 32) != 32) return false;
    memcpy(challenge, value, 32); challenge[32] = '\0';
    if (!buddy_auth_is_hex(challenge, 32)) return false;
    value += 32;
    if (strncmp(value, ",n=", 3) != 0) return false;
    value += 3;
    uint64_t number = 0;
    size_t digits = 0;
    while (value[digits] >= '0' && value[digits] <= '9' && digits < 11)
        number = number * 10 + (uint64_t)(value[digits++] - '0');
    if (digits == 0 || digits > 10 || value[0] == '0' || number > UINT32_MAX) return false;
    *counter = (uint32_t)number;
    value += digits;
    if (strncmp(value, ",s=", 3) != 0) return false;
    value += 3;
    if (strlen(value) != 64 || !buddy_auth_is_hex(value, 64)) return false;
    memcpy(signature, value, 65);
    return true;
}

buddy_auth_result_t buddy_auth_verify(const char *authorization, const char *method, const char *uri,
                                      const char *body_sha256, int64_t now_us)
{
    char challenge[33], signature[65];
    uint32_t counter;
    if (!parse_authorization(authorization, challenge, &counter, signature) || !method || !uri ||
        strchr(method, '\n') || strchr(uri, '\n') || !buddy_auth_is_hex(body_sha256, 64))
        return BUDDY_AUTH_MALFORMED;
    char message[512];
    int length = snprintf(message, sizeof(message), "clawpal-auth-v2\n%s\n%s\n%s\n%lu\n%s",
                          method, uri, challenge, (unsigned long)counter, body_sha256);
    if (length <= 0 || (size_t)length >= sizeof(message)) return BUDDY_AUTH_MALFORMED;
    char expected[65];
    if (!hmac_hex(message, (size_t)length, expected)) return BUDDY_AUTH_BAD_SIGNATURE;
    unsigned char difference = 0;
    for (size_t i = 0; i < 64; ++i) difference |= (unsigned char)(expected[i] ^ signature[i]);

    buddy_auth_result_t result = BUDDY_AUTH_UNKNOWN_CHALLENGE;
    lock();
    for (size_t i = 0; i < BUDDY_AUTH_CHALLENGES; ++i) {
        auth_challenge_t *slot = &challenges[i];
        if (slot->expires_us == 0 || strcmp(slot->challenge, challenge) != 0) continue;
        if (now_us >= slot->expires_us) result = BUDDY_AUTH_UNKNOWN_CHALLENGE;
        else if (difference) result = BUDDY_AUTH_BAD_SIGNATURE;
        else if (counter <= slot->last_counter) result = BUDDY_AUTH_REPLAYED;
        else { slot->last_counter = counter; result = BUDDY_AUTH_OK; }
        break;
    }
    unlock();
    return result;
}

bool buddy_auth_body_hash(const void *data, size_t length, char out[65])
{
    uint8_t digest[32];
    if (mbedtls_sha256((const unsigned char *)data, length, digest, 0) != 0) return false;
    hex(digest, sizeof(digest), out);
    return true;
}
