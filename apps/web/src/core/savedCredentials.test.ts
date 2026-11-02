import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createFakeStorage, createMemoryStorage } from '../test/fixtures'
import {
  SAVED_LOGIN_KEY,
  clearSavedLogin,
  loadSavedLogin,
  saveSavedLogin,
} from './savedCredentials'

let storage: Storage

beforeEach(() => {
  storage = createMemoryStorage()
  vi.stubGlobal('localStorage', storage)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('savedCredentials', () => {
  it('保存后可往返读取，且邮箱去首尾空格', () => {
    saveSavedLogin({ email: '  alice@example.com  ', password: 'Secret123' })

    expect(loadSavedLogin()).toEqual({ email: 'alice@example.com', password: 'Secret123' })
  })

  it('只写入 email/password 两个字段', () => {
    saveSavedLogin({ email: 'bob@example.com', password: 'Passw0rd!' })

    const raw = storage.getItem(SAVED_LOGIN_KEY)
    expect(raw).not.toBeNull()
    expect(Object.keys(JSON.parse(raw as string))).toEqual(['email', 'password'])
  })

  it('无记录时返回 null', () => {
    expect(loadSavedLogin()).toBeNull()
  })

  it('损坏 JSON 时返回 null 且不抛错', () => {
    storage.setItem(SAVED_LOGIN_KEY, '{not-json')

    expect(() => loadSavedLogin()).not.toThrow()
    expect(loadSavedLogin()).toBeNull()
  })

  it('缺少字段时返回 null', () => {
    storage.setItem(SAVED_LOGIN_KEY, JSON.stringify({ email: 'carol@example.com' }))
    expect(loadSavedLogin()).toBeNull()

    storage.setItem(SAVED_LOGIN_KEY, JSON.stringify({ email: 'carol@example.com', password: 123 }))
    expect(loadSavedLogin()).toBeNull()
  })

  it('空密码或空邮箱不写入、也读不出', () => {
    saveSavedLogin({ email: 'dave@example.com', password: '' })
    expect(storage.getItem(SAVED_LOGIN_KEY)).toBeNull()

    saveSavedLogin({ email: '   ', password: 'Passw0rd!' })
    expect(storage.getItem(SAVED_LOGIN_KEY)).toBeNull()

    storage.setItem(SAVED_LOGIN_KEY, JSON.stringify({ email: '', password: 'Passw0rd!' }))
    expect(loadSavedLogin()).toBeNull()
  })

  it('清除后读取为 null', () => {
    saveSavedLogin({ email: 'erin@example.com', password: 'Passw0rd!' })
    expect(loadSavedLogin()).not.toBeNull()

    clearSavedLogin()
    expect(loadSavedLogin()).toBeNull()
  })

  it('localStorage 不可用（读/写抛错）时静默降级', () => {
    const broken = createFakeStorage({}, { failRead: true, failWrite: true })
    vi.stubGlobal('localStorage', broken)

    expect(() => saveSavedLogin({ email: 'frank@example.com', password: 'Passw0rd!' })).not.toThrow()
    expect(loadSavedLogin()).toBeNull()
    expect(() => clearSavedLogin()).not.toThrow()
  })
})
