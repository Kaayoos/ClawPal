/* Host stand-in for mbedtls_md_hmac with SHA-256 (RFC 2104). */
#pragma once
#include "mbedtls/sha256.h"

typedef enum { MBEDTLS_MD_SHA256 = 9 } mbedtls_md_type_t;
typedef struct { mbedtls_md_type_t type; } mbedtls_md_info_t;

static inline const mbedtls_md_info_t *mbedtls_md_info_from_type(mbedtls_md_type_t type)
{
    static const mbedtls_md_info_t sha256 = {MBEDTLS_MD_SHA256};
    return type == MBEDTLS_MD_SHA256 ? &sha256 : NULL;
}

static inline int mbedtls_md_hmac(const mbedtls_md_info_t *info, const unsigned char *key, size_t key_length,
                                  const unsigned char *input, size_t length, unsigned char *output)
{
    uint8_t block[64] = {0}, inner[32];
    host_sha256_t s;
    if (!info) return -1;
    if (key_length > 64) mbedtls_sha256(key, key_length, block, 0);
    else memcpy(block, key, key_length);
    for (int i = 0; i < 64; ++i) block[i] ^= 0x36;
    host_sha256_start(&s); host_sha256_update(&s, block, 64); host_sha256_update(&s, input, length); host_sha256_finish(&s, inner);
    for (int i = 0; i < 64; ++i) block[i] ^= 0x36 ^ 0x5c;
    host_sha256_start(&s); host_sha256_update(&s, block, 64); host_sha256_update(&s, inner, 32); host_sha256_finish(&s, output);
    return 0;
}
