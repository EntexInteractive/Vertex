import {type FormEvent, useState} from 'react'
import {Link, useNavigate} from 'react-router'
import {useSession} from '../../app/session'
import {Icon} from '../../components/Icon'
import {call, hasDesktopHost, host, HostError} from '../../host/client'

/** Log in (the same login as `vx login`), or, when logged in, see who you are and log out. */
export function AccountPage() {
    const {session} = useSession()

    return (
        <main className="login-page">
            <section className="login-brand-panel">
                <div className="login-brand">
                    <span className="login-mark">V</span>
                    <span>Vertex</span>
                </div>
                <div className="login-pitch">
                    <span className="login-kicker">CENTRALIZED VERSION CONTROL</span>
                    <h1>
                        Your work.
                        <br/>
                        One source of truth.
                    </h1>
                    <p>Lock, submit and sync multi-gigabyte assets from one workspace, with one linear history on the server.</p>
                    <div className="login-feature">
                        <span>
                            <Icon name="lock" size={18}/>
                        </span>
                        <div>
                            <b>Built for big projects</b>
                            <small>Large binaries, automatic file locking and fast syncs.</small>
                        </div>
                    </div>
                </div>
                <div className="login-orbit" aria-hidden="true">
                    <i/>
                    <i/>
                    <i/>
                    <i/>
                    <span className="orbit-core">V</span>
                </div>
                {session && (
                    <div className="login-status">
                        <i/>
                        {session.server}
                    </div>
                )}
            </section>

            <section className="login-form-panel">
                <div className="login-form-wrap">
                    {session?.loggedIn ? <SignedIn/> : <LoginForm/>}
                </div>
                <footer>Vertex Desktop</footer>
            </section>
        </main>
    )
}

function LoginForm() {
    const {session, setSession} = useSession()
    const navigate = useNavigate()
    const [server, setServer] = useState(session?.server ?? '')
    const [username, setUsername] = useState('')
    const [password, setPassword] = useState('')
    const [showPassword, setShowPassword] = useState(false)
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState<string>()

    const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
        event.preventDefault()
        setBusy(true)
        setError(undefined)
        try {
            const next = await call(host.POST('/api/session/login', {body: {server, username, password}}))
            setSession(next)
            navigate('/changes')
        } catch (e) {
            setError(e instanceof HostError ? e.message : String(e))
        } finally {
            setBusy(false)
        }
    }

    if (!hasDesktopHost()) {
        return (
            <div className="login-heading">
                <span className="login-mobile-mark">V</span>
                <h2>Open Vertex Desktop</h2>
                <p>Logging in from the browser isn't available yet. Use the Vertex desktop app or run 'vx login'.</p>
            </div>
        )
    }

    return (
        <>
            <div className="login-heading">
                <span className="login-mobile-mark">V</span>
                <h2>Log in</h2>
                <p>The app and the vx command line share this login.</p>
            </div>

            <form onSubmit={handleSubmit}>
                <label>
                    <span>Server</span>
                    <div className="login-input">
                        <Icon name="cloud" size={16}/>
                        <input value={server} onChange={e => setServer(e.target.value)} placeholder="https://vertex.example.com" required/>
                    </div>
                </label>
                <label>
                    <span>Username</span>
                    <div className="login-input">
                        <Icon name="user" size={16}/>
                        <input value={username} onChange={e => setUsername(e.target.value)} autoComplete="username" autoFocus required/>
                    </div>
                </label>
                <label>
                    <span>Password</span>
                    <div className="login-input">
                        <Icon name="lock" size={16}/>
                        <input
                            type={showPassword ? 'text' : 'password'}
                            value={password}
                            onChange={e => setPassword(e.target.value)}
                            autoComplete="current-password"
                            required
                        />
                        <button type="button" onClick={() => setShowPassword(current => !current)}>
                            {showPassword ? 'Hide' : 'Show'}
                        </button>
                    </div>
                </label>
                {error && <p className="login-error" role="alert">{error}</p>}
                <button className="login-submit" type="submit" disabled={busy}>
                    {busy ? 'Logging in…' : 'Log in'} <Icon name="chevron" size={15}/>
                </button>
            </form>
            <p className="login-create">No account yet? A server admin adds you with 'vx user add'.</p>
        </>
    )
}

function SignedIn() {
    const {session, setSession} = useSession()
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState<string>()

    const logOut = async () => {
        setBusy(true)
        setError(undefined)
        try {
            setSession(await call(host.POST('/api/session/logout')))
        } catch (e) {
            setError(e instanceof HostError ? e.message : String(e))
        } finally {
            setBusy(false)
        }
    }

    return (
        <>
            <div className="login-heading">
                <span className="login-mobile-mark">V</span>
                <h2>Logged in as {session?.username}</h2>
                <p>On {session?.server}. The vx command line uses the same login.</p>
            </div>
            <Link className="login-submit" to="/changes">
                Continue to Vertex <Icon name="chevron" size={15}/>
            </Link>
            {error && <p className="login-error" role="alert">{error}</p>}
            <div className="login-sso">
                <button type="button" onClick={logOut} disabled={busy}>
                    <Icon name="logout" size={15}/>
                    {busy ? 'Logging out…' : 'Log out'}
                </button>
            </div>
        </>
    )
}
