/* Host checks for signed LAN requests. Included by test_parsers.c, which supplies check().
 * The expected signatures were computed independently with Python's hmac module and
 * are repeated in the companion's protocol tests, so both sides must agree. */
#include "buddy_auth.h"
#include "mbedtls/md.h"

#define AUTH_TOKEN "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
#define AUTH_CHALLENGE "0123456789abcdef0123456789abcdef"
#define AUTH_GET "ClawPal-HMAC c=" AUTH_CHALLENGE ",n=1,s=7572b295b456924266bca3396a16c3008767057b3535fbe493523fc42a88a11c"
#define AUTH_POST_BODY "82e9ccc751483310ef92e697844b03dcdb459ea5ebeb57bbfd0264b7a3450482"
#define AUTH_POST "ClawPal-HMAC c=" AUTH_CHALLENGE ",n=7,s=d9da15113a88771cdffded553fb8a99c00f3136e092280bc26c062351d8dd04c"

static const uint8_t auth_random[16] = {0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef, 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef};

static void auth_checks(void)
{
    char hash[65], challenge[33], proof[65];
    check(buddy_auth_body_hash("abc", 3, hash) && !strcmp(hash, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad") &&
          buddy_auth_body_hash("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", 56, hash) &&
          !strcmp(hash, "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1") &&
          buddy_auth_body_hash("", 0, hash) && !strcmp(hash, BUDDY_AUTH_EMPTY_BODY), "auth: SHA-256 matches FIPS 180-4 vectors");
    check(buddy_auth_body_hash("{\"action\":\"wifi\"}", 17, hash) && !strcmp(hash, AUTH_POST_BODY), "auth: body hash matches the companion");

    buddy_auth_init(AUTH_TOKEN, NULL, NULL);
    buddy_auth_challenge("fedcba9876543210fedcba9876543210", auth_random, 1000, challenge);
    check(!strcmp(challenge, AUTH_CHALLENGE), "auth: challenge is the hex of its random bytes");
    check(buddy_auth_hello_proof("fedcba9876543210fedcba9876543210", "001122334455", 42171, challenge, proof) &&
          !strcmp(proof, "d6c75188c12b1d1a23ef825dd6941504cab7c94a30df0dce5eaf47918743709a"), "auth: discovery proof covers the challenge");
    char again[33];
    const uint8_t other[16] = {0xff};
    buddy_auth_challenge("fedcba9876543210fedcba9876543210", other, 2000, again);
    check(!strcmp(again, challenge), "auth: repeated discovery nonce reuses its challenge");

    check(buddy_auth_verify(AUTH_GET, "GET", "/settings", BUDDY_AUTH_EMPTY_BODY, 2000) == BUDDY_AUTH_OK, "auth: companion-signed GET is accepted");
    check(buddy_auth_verify(AUTH_GET, "GET", "/settings", BUDDY_AUTH_EMPTY_BODY, 3000) == BUDDY_AUTH_REPLAYED, "auth: the same request cannot be replayed");
    check(buddy_auth_verify(AUTH_POST, "POST", "/confirm", AUTH_POST_BODY, 3000) == BUDDY_AUTH_OK, "auth: a later counter is accepted, gaps allowed");
    check(buddy_auth_verify(AUTH_GET, "GET", "/settings", BUDDY_AUTH_EMPTY_BODY, 3000) == BUDDY_AUTH_REPLAYED, "auth: an older counter is refused");

    buddy_auth_init(AUTH_TOKEN, NULL, NULL);
    buddy_auth_challenge("fedcba9876543210fedcba9876543210", auth_random, 1000, challenge);
    check(buddy_auth_verify(AUTH_POST, "POST", "/settings", AUTH_POST_BODY, 2000) == BUDDY_AUTH_BAD_SIGNATURE, "auth: signature is bound to the path");
    check(buddy_auth_verify(AUTH_POST, "GET", "/confirm", AUTH_POST_BODY, 2000) == BUDDY_AUTH_BAD_SIGNATURE, "auth: signature is bound to the method");
    check(buddy_auth_verify(AUTH_POST, "POST", "/confirm", BUDDY_AUTH_EMPTY_BODY, 2000) == BUDDY_AUTH_BAD_SIGNATURE, "auth: signature is bound to the body");
    check(buddy_auth_verify("Bearer " AUTH_TOKEN, "GET", "/settings", BUDDY_AUTH_EMPTY_BODY, 2000) == BUDDY_AUTH_MALFORMED, "auth: the bearer token alone is no longer accepted");
    check(buddy_auth_verify(AUTH_POST, "POST", "/confirm", AUTH_POST_BODY, 1000 + BUDDY_AUTH_CHALLENGE_US) == BUDDY_AUTH_UNKNOWN_CHALLENGE, "auth: expired challenge is refused");
    check(buddy_auth_verify(AUTH_POST, "POST", "/confirm", AUTH_POST_BODY, 2000) == BUDDY_AUTH_OK, "auth: valid request still works before expiry");

    buddy_auth_init(AUTH_TOKEN, NULL, NULL);
    check(buddy_auth_verify(AUTH_GET, "GET", "/settings", BUDDY_AUTH_EMPTY_BODY, 2000) == BUDDY_AUTH_UNKNOWN_CHALLENGE, "auth: challenges do not survive a restart");
    buddy_auth_init("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", NULL, NULL);
    buddy_auth_challenge("fedcba9876543210fedcba9876543210", auth_random, 1000, challenge);
    check(buddy_auth_verify(AUTH_GET, "GET", "/settings", BUDDY_AUTH_EMPTY_BODY, 2000) == BUDDY_AUTH_BAD_SIGNATURE, "auth: another pairing token cannot sign");

    buddy_auth_init(AUTH_TOKEN, NULL, NULL);
    for (unsigned i = 0; i < BUDDY_AUTH_CHALLENGES; ++i) {
        char nonce[33];
        uint8_t random[16] = {(uint8_t)(i + 1)};
        snprintf(nonce, sizeof(nonce), "%032x", i + 1);
        buddy_auth_challenge(nonce, random, 1000, challenge);
    }
    buddy_auth_challenge("fedcba9876543210fedcba9876543210", auth_random, 1000, challenge);
    check(buddy_auth_verify(AUTH_GET, "GET", "/settings", BUDDY_AUTH_EMPTY_BODY, 2000) == BUDDY_AUTH_OK, "auth: newest challenge survives a full table");

    const char *malformed[] = {
        "", "ClawPal-HMAC", "ClawPal-HMAC c=0123", "clawpal-hmac c=" AUTH_CHALLENGE ",n=1,s=7572b295b456924266bca3396a16c3008767057b3535fbe493523fc42a88a11c",
        "ClawPal-HMAC c=" AUTH_CHALLENGE ",n=0,s=7572b295b456924266bca3396a16c3008767057b3535fbe493523fc42a88a11c",
        "ClawPal-HMAC c=" AUTH_CHALLENGE ",n=01,s=7572b295b456924266bca3396a16c3008767057b3535fbe493523fc42a88a11c",
        "ClawPal-HMAC c=" AUTH_CHALLENGE ",n=4294967296,s=7572b295b456924266bca3396a16c3008767057b3535fbe493523fc42a88a11c",
        "ClawPal-HMAC c=" AUTH_CHALLENGE ",n=1,s=7572B295B456924266BCA3396A16C3008767057B3535FBE493523FC42A88A11C",
        AUTH_GET " ", AUTH_GET ",x=1",
    };
    bool rejected = true;
    for (size_t i = 0; i < sizeof(malformed) / sizeof(malformed[0]); ++i)
        rejected = rejected && buddy_auth_verify(malformed[i], "GET", "/settings", BUDDY_AUTH_EMPTY_BODY, 2000) == BUDDY_AUTH_MALFORMED;
    check(rejected, "auth: malformed, zero, padded, oversized and uppercase headers are rejected");
    check(buddy_auth_verify(AUTH_GET, "GET", "/settings\nX", BUDDY_AUTH_EMPTY_BODY, 2000) == BUDDY_AUTH_MALFORMED &&
          buddy_auth_verify(AUTH_GET, "GET", "/settings", "not-a-hash", 2000) == BUDDY_AUTH_MALFORMED,
          "auth: line breaks and bad body hashes cannot reshape the signed text");
}
