/* Host stand-in for the two mbedtls SHA-256 calls the firmware's auth code uses.
 * Straight FIPS 180-4; the tests check it against published vectors. */
#pragma once
#include <stddef.h>
#include <stdint.h>
#include <string.h>

typedef struct { uint32_t h[8]; uint64_t length; uint8_t block[64]; size_t used; } host_sha256_t;

static inline uint32_t host_sha256_rotr(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }

static inline void host_sha256_block(host_sha256_t *s, const uint8_t *p)
{
    static const uint32_t k[64] = {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};
    uint32_t w[64], v[8];
    for (int i = 0; i < 16; ++i) w[i] = (uint32_t)p[i * 4] << 24 | (uint32_t)p[i * 4 + 1] << 16 | (uint32_t)p[i * 4 + 2] << 8 | p[i * 4 + 3];
    for (int i = 16; i < 64; ++i) {
        uint32_t s0 = host_sha256_rotr(w[i - 15], 7) ^ host_sha256_rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
        uint32_t s1 = host_sha256_rotr(w[i - 2], 17) ^ host_sha256_rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    memcpy(v, s->h, sizeof(v));
    for (int i = 0; i < 64; ++i) {
        uint32_t t1 = v[7] + (host_sha256_rotr(v[4], 6) ^ host_sha256_rotr(v[4], 11) ^ host_sha256_rotr(v[4], 25)) +
                      ((v[4] & v[5]) ^ (~v[4] & v[6])) + k[i] + w[i];
        uint32_t t2 = (host_sha256_rotr(v[0], 2) ^ host_sha256_rotr(v[0], 13) ^ host_sha256_rotr(v[0], 22)) +
                      ((v[0] & v[1]) ^ (v[0] & v[2]) ^ (v[1] & v[2]));
        memmove(v + 1, v, 7 * sizeof(uint32_t));
        v[4] += t1;
        v[0] = t1 + t2;
    }
    for (int i = 0; i < 8; ++i) s->h[i] += v[i];
}

static inline void host_sha256_start(host_sha256_t *s)
{
    static const uint32_t initial[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    memcpy(s->h, initial, sizeof(initial));
    s->length = 0;
    s->used = 0;
}

static inline void host_sha256_update(host_sha256_t *s, const uint8_t *data, size_t length)
{
    s->length += length;
    while (length--) {
        s->block[s->used++] = *data++;
        if (s->used == 64) { host_sha256_block(s, s->block); s->used = 0; }
    }
}

static inline void host_sha256_finish(host_sha256_t *s, uint8_t out[32])
{
    uint64_t bits = s->length * 8;
    uint8_t pad = 0x80, zero = 0, size[8];
    host_sha256_update(s, &pad, 1);
    while (s->used != 56) host_sha256_update(s, &zero, 1);
    for (int i = 0; i < 8; ++i) size[i] = (uint8_t)(bits >> (56 - i * 8));
    host_sha256_update(s, size, 8);
    for (int i = 0; i < 8; ++i) {
        out[i * 4] = (uint8_t)(s->h[i] >> 24); out[i * 4 + 1] = (uint8_t)(s->h[i] >> 16);
        out[i * 4 + 2] = (uint8_t)(s->h[i] >> 8); out[i * 4 + 3] = (uint8_t)s->h[i];
    }
}

static inline int mbedtls_sha256(const unsigned char *input, size_t length, unsigned char output[32], int is224)
{
    host_sha256_t s;
    if (is224) return -1;
    host_sha256_start(&s);
    host_sha256_update(&s, input, length);
    host_sha256_finish(&s, output);
    return 0;
}
