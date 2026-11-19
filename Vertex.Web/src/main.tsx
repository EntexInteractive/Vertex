// Must be the first import: it takes the desktop app's token out of the URL (#token=…) before the hash router reads
// the URL.
import './host/client'

import {StrictMode} from 'react'
import {createRoot} from 'react-dom/client'
import App from './app/App'
import './styles/index.css'

createRoot(document.getElementById('root')!).render(
    <StrictMode>
        <App/>
    </StrictMode>,
)
