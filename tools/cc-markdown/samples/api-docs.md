# REST API Documentation

## Authentication

All API requests require authentication via Bearer token.

```bash
curl -H "Authorization: Bearer YOUR_TOKEN" https://api.example.com/v1/users
```

## Endpoints

### GET /users

Retrieve a list of all users.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| limit | integer | No | Max results (default: 20) |
| offset | integer | No | Pagination offset |
| status | string | No | Filter by status |

**Response:**

```json
{
  "data": [
    {
      "id": "usr_123",
      "email": "user@example.com",
      "created_at": "2025-01-15T10:30:00Z"
    }
  ],
  "meta": {
    "total": 150,
    "limit": 20,
    "offset": 0
  }
}
```

### POST /users

Create a new user account.

**Request Body:**

```json
{
  "email": "newuser@example.com",
  "name": "John Doe",
  "role": "member"
}
```

**Response Codes:**

| Code | Description |
|------|-------------|
| 201 | User created successfully |
| 400 | Invalid request body |
| 409 | Email already exists |

## Rate Limits

- **Standard tier:** 100 requests/minute
- **Pro tier:** 1000 requests/minute
- **Enterprise:** Unlimited

> Exceeding rate limits returns HTTP 429 with a `Retry-After` header.

## Error Handling

All errors follow this format:

```json
{
  "error": {
    "code": "INVALID_TOKEN",
    "message": "The provided token has expired",
    "details": {}
  }
}
```

---

Version 2.1.0 | Last updated: January 2025
